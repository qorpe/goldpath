using Goldpath;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The database-backed store on a real database: requests, trails and delegations SURVIVE the
/// store instance — a "restarted" engine over the same database sees the same worklist, which is
/// exactly what the in-memory store cannot promise. The facts are provider-neutral: SQLite runs
/// them here (<see cref="EfApprovalStoreTests"/>), Oracle runs the same file in the integration
/// project's Oracle lane (G7, O-5b, #410).
/// </summary>
public abstract class EfApprovalStoreFacts : IAsyncLifetime
{
    public sealed class ApprovalsDbContext(DbContextOptions<ApprovalsDbContext> options) : DbContext(options)
    {
        // SQLite cannot ORDER BY a raw DateTimeOffset column; the family answer (same as
        // the Bulk/Archival test contexts) is the binary converter convention. Only there:
        // every other provider stores the offset natively.
        private readonly bool _sqlite = options.Extensions.Any(e => e.GetType().Namespace == "Microsoft.EntityFrameworkCore.Sqlite.Infrastructure.Internal");

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        {
            if (_sqlite)
            {
                configurationBuilder.Properties<DateTimeOffset>()
                    .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.AddGoldpathApprovalModel();
    }

    /// <summary>
    /// A clock that runs a hook the next time the engine reads it. The engine reads the clock after it has read the
    /// request and before it writes, so the hook lands another write exactly in that gap, on any store.
    /// </summary>
    public sealed class RacingClock(DateTimeOffset now) : TimeProvider
    {
        public Func<Task>? OnNextRead { get; set; }

        public override DateTimeOffset GetUtcNow()
        {
            var hook = OnNextRead;
            OnNextRead = null;
            hook?.Invoke().GetAwaiter().GetResult();
            return now;
        }
    }

    private ServiceProvider _provider = null!;

    /// <summary>Opens an empty database and returns the provider configuration that reaches it.</summary>
    protected abstract Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync();

    /// <summary>Releases what <see cref="OpenDatabaseAsync"/> opened.</summary>
    protected virtual Task CloseDatabaseAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var useProvider = await OpenDatabaseAsync();
        _provider = new ServiceCollection()
            .AddDbContext<ApprovalsDbContext>(useProvider)
            .BuildServiceProvider(true);
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApprovalsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await CloseDatabaseAsync();
    }

    private GoldpathApprovalEngine BuildEngine(TimeProvider clock) => new(
        new GoldpathApprovalsOptions().AddLadder("credit-limit", l => l
            .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
            .TopRung("general-manager", TimeSpan.FromHours(24))),
        new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
        clock, NullLogger<GoldpathApprovalEngine>.Instance);

    [Fact]
    public async Task Requests_survive_the_engine_a_restarted_engine_sees_the_same_worklist()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var first = BuildEngine(clock);
        var request = await first.RequestAsync("credit-limit", "K26-100", 500_000m, "maker");

        // A brand-new engine + store over the SAME database — the restart.
        var restarted = BuildEngine(clock);
        var worklist = await restarted.WorklistAsync("checker", "expert");
        Assert.Equal([request.Id], worklist.Select(r => r.Id));

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await restarted.DecideAsync(request.Id, "checker", "expert", true, "fits"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await Reload(request.Id)).Status);
    }

    [Fact]
    public async Task The_trail_round_trips_through_the_json_column()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var engine = BuildEngine(clock);
        var request = await engine.RequestAsync("credit-limit", "K26-101", 500_000m, "maker");
        clock.Advance(TimeSpan.FromHours(8));
        await engine.EscalateOverdueAsync();
        await engine.DecideAsync(request.Id, "gm", "general-manager", false, "declined on review");

        var reloaded = await Reload(request.Id);
        Assert.Equal(["requested", "escalated", "rejected"], reloaded.Trail.Select(t => t.Action));
        Assert.Equal("declined on review", reloaded.Reason);
    }

    [Fact]
    public async Task Delegations_persist_and_expire_by_the_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var engine = BuildEngine(clock);
        var request = await engine.RequestAsync("credit-limit", "K26-102", 500_000m, "maker");
        await engine.DelegateAsync("expert-user", "stand-in", TimeSpan.FromDays(2));

        // A restarted engine honors the persisted delegation...
        var restarted = BuildEngine(clock);
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await restarted.DecideAsync(request.Id, "stand-in", "no-role", true, "delegated"));

        // ...and its expiry.
        var late = await restarted.RequestAsync("credit-limit", "K26-103", 500_000m, "maker");
        clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));
        Assert.Equal(GoldpathApprovalDecisionOutcome.WrongRole,
            await restarted.DecideAsync(late.Id, "stand-in", "no-role", true, "too late"));
    }

    [Fact]
    public async Task Quorum_signatures_survive_the_engine_a_restarted_engine_completes_the_rung()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-26T09:00:00Z"));
        var quorumOptions = new GoldpathApprovalsOptions().AddLadder("payment-run", l => l
            .Rung("manager", 5_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
            .TopRung("general-manager", TimeSpan.FromHours(24)));
        GoldpathApprovalEngine BuildQuorumEngine() => new(
            quorumOptions,
            new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
            clock, NullLogger<GoldpathApprovalEngine>.Instance);

        var first = BuildQuorumEngine();
        var request = await first.RequestAsync("payment-run", "K26-104", 1_000_000m, "maker");
        await first.DecideAsync(request.Id, "manager-one", "manager", true, "first signature");

        // A brand-new engine over the SAME database: the collected signature is still there —
        // the same signer is barred, a distinct one completes the rung.
        var restarted = BuildQuorumEngine();
        Assert.Equal(GoldpathApprovalDecisionOutcome.AlreadySigned,
            await restarted.DecideAsync(request.Id, "manager-one", "manager", true, "again"));
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await restarted.DecideAsync(request.Id, "manager-two", "manager", true, "second signature"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await Reload(request.Id)).Status);
    }

    [Fact]
    public async Task Recent_comes_newest_first_and_honors_take_on_the_database_store()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-28T09:00:00Z"));
        var engine = BuildEngine(clock);
        var first = await engine.RequestAsync("credit-limit", "K26-200", 500_000m, "maker");
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await engine.RequestAsync("credit-limit", "K26-201", 500_000m, "maker");
        clock.Advance(TimeSpan.FromMinutes(1));
        var third = await engine.RequestAsync("credit-limit", "K26-202", 500_000m, "maker");

        var store = new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        var recent = await store.GetRecentAsync(2);
        Assert.Equal([third.Id, second.Id], recent.Select(r => r.Id));
        _ = first;
    }

    [Fact]
    public async Task Withdraw_and_the_resubmit_chain_round_trip_through_the_database()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-26T09:00:00Z"));
        var engine = BuildEngine(clock);

        var withdrawn = await engine.RequestAsync("credit-limit", "K26-105", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.WithdrawAsync(withdrawn.Id, "maker"));
        Assert.Equal(GoldpathApprovalStatus.Withdrawn, (await Reload(withdrawn.Id)).Status);

        var rejected = await engine.RequestAsync("credit-limit", "K26-106", 500_000m, "maker");
        await engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing");
        var resubmitted = await engine.ResubmitAsync(rejected.Id, "maker");

        var reloaded = await Reload(resubmitted.Id);
        Assert.Equal(rejected.Id, reloaded.SupersedesId);
        Assert.Equal(["requested", "rejected", "superseded"], (await Reload(rejected.Id)).Trail.Select(t => t.Action));
    }

    [Fact]
    public async Task The_store_refuses_a_second_signature_by_the_same_identity_on_one_request()
    {
        // #480: the database holds distinct-eyes too, so a double click cannot slip two signatures between the
        // engine's check and its write. Proven on the store itself: the engine's own check would hide the key.
        var store = Store();
        var at = DateTimeOffset.Parse("2026-11-03T09:00:00Z");
        var request = Guid.NewGuid();
        await store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker", "expert", at));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker", "general-manager", at.AddMinutes(1))));
        Assert.Equal(["checker"], (await store.GetSignaturesAsync(request)).Select(s => s.SignedBy));

        // Only the pair is unique: another identity on this request, and this identity on another request, still sign.
        var other = Guid.NewGuid();
        await store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker-2", "expert", at.AddMinutes(2)));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(other, "checker", "expert", at.AddMinutes(3)));
        Assert.Equal(2, (await store.GetSignaturesAsync(request)).Count);
        Assert.Single(await store.GetSignaturesAsync(other));
    }

    [Fact]
    public async Task A_write_from_a_stale_read_is_refused_and_the_write_that_won_stands()
    {
        // #480: two reads of one request, two writes. The second was decided on a state that no longer exists.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var created = await BuildEngine(clock).RequestAsync("credit-limit", "K26-480", 500_000m, "maker");
        var store = Store();
        var first = await Reload(created.Id);
        var second = await Reload(created.Id);

        first.Status = GoldpathApprovalStatus.Rejected;
        first.Trail.Add(new GoldpathApprovalTrailEntry(clock.GetUtcNow(), "checker-a", "rejected", "no"));
        await store.UpdateAsync(first);

        second.Status = GoldpathApprovalStatus.Granted;
        second.Trail.Add(new GoldpathApprovalTrailEntry(clock.GetUtcNow(), "checker-b", "granted", "yes"));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.UpdateAsync(second));

        var stored = await Reload(created.Id);
        Assert.Equal(GoldpathApprovalStatus.Rejected, stored.Status);
        Assert.Equal(["requested", "rejected"], stored.Trail.Select(t => t.Action));

        // A fresh read writes, and so does the next one after it: the version moves with every write.
        stored.Trail.Add(new GoldpathApprovalTrailEntry(clock.GetUtcNow(), "maker", "noted", "1"));
        await store.UpdateAsync(stored);
        var again = await Reload(created.Id);
        again.Trail.Add(new GoldpathApprovalTrailEntry(clock.GetUtcNow(), "maker", "noted", "2"));
        await store.UpdateAsync(again);
        Assert.Equal(["requested", "rejected", "noted", "noted"], (await Reload(created.Id)).Trail.Select(t => t.Action));
    }

    [Fact]
    public async Task A_grant_that_loses_to_a_reject_between_its_read_and_its_write_answers_NotPending_and_signs_nothing()
    {
        // #480 on the database store, made deterministic: the reject lands in the gap the grant's clock read marks.
        var clock = new RacingClock(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var engine = BuildEngine(clock);
        var request = await engine.RequestAsync("credit-limit", "K26-486", 500_000m, "maker");
        clock.OnNextRead = async () => Assert.Equal(
            GoldpathApprovalDecisionOutcome.Applied,
            await BuildEngine(new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z"))).DecideAsync(request.Id, "checker-b", "expert", false, "no"));

        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending, await engine.DecideAsync(request.Id, "checker-a", "expert", true, "yes"));
        Assert.Empty(await Store().GetSignaturesAsync(request.Id));
        var stored = await Reload(request.Id);
        Assert.Equal(GoldpathApprovalStatus.Rejected, stored.Status);
        Assert.Equal(["requested", "rejected"], stored.Trail.Select(t => t.Action));
    }

    private GoldpathEfApprovalStore<ApprovalsDbContext> Store()
        => new(_provider.GetRequiredService<IServiceScopeFactory>());

    private async Task<GoldpathApprovalRequest> Reload(Guid id)
    {
        var store = new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        return await store.GetAsync(id) ?? throw new InvalidOperationException("missing request");
    }
}
