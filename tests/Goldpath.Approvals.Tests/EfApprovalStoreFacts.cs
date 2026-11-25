using Goldpath;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    /// <summary>
    /// Fails the next command that updates a row, once and before it runs (#521): a timeout, a dropped connection, the
    /// bounded retry giving up. Whatever the same save wrote before it rolls back with it; a write saved on its own
    /// before it stays, which is the failure the two-write resubmission could not recover from.
    /// </summary>
    public sealed class FailNextUpdate : DbCommandInterceptor
    {
        public bool Armed { get; set; }

        private void Fail(DbCommand command)
        {
            if (Armed && command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                Armed = false;
                throw new TimeoutException("The update timed out (injected).");
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Fail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Fail(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Fail(command);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// Runs a hook once, before the next save begins its transaction: another write lands between the engine's read
    /// and its single write, so the write itself loses the race (#521).
    /// </summary>
    public sealed class BeforeNextSave : SaveChangesInterceptor
    {
        public Func<Task>? Hook { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var hook = Hook;
            Hook = null;
            if (hook is not null)
            {
                await hook();
            }

            return result;
        }
    }

    /// <summary>Records what the engine publishes.</summary>
    public sealed class Published : IIntegrationEventPublisher
    {
        public List<IIntegrationEvent> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private readonly FailNextUpdate _failNextUpdate = new();

    private readonly BeforeNextSave _beforeNextSave = new();

    private ServiceProvider _provider = null!;

    /// <summary>Opens an empty database and returns the provider configuration that reaches it.</summary>
    protected abstract Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync();

    /// <summary>Releases what <see cref="OpenDatabaseAsync"/> opened.</summary>
    protected virtual Task CloseDatabaseAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var useProvider = await OpenDatabaseAsync();
        _provider = new ServiceCollection()
            .AddDbContext<ApprovalsDbContext>(options =>
            {
                useProvider(options);
                options.AddInterceptors(_failNextUpdate, _beforeNextSave);
            })
            .BuildServiceProvider(true);
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ApprovalsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await CloseDatabaseAsync();
    }

    private GoldpathApprovalEngine BuildEngine(TimeProvider clock, IIntegrationEventPublisher? published = null) => new(
        new GoldpathApprovalsOptions().AddLadder("credit-limit", l => l
            .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
            .TopRung("general-manager", TimeSpan.FromHours(24))),
        new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
        clock, NullLogger<GoldpathApprovalEngine>.Instance, published);

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
        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(2));

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
    public async Task The_store_refuses_a_second_request_that_supersedes_the_same_rejection()
    {
        // #521: the unique SupersedesId index holds "resubmitted once" for two resubmissions at once, which both pass the
        // engine's trail check. Proven on the store itself, and the refusal names the rework that landed first.
        var store = Store();
        var rejected = Guid.NewGuid();
        var first = Row(rejected);
        await store.AddAsync(first);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.AddAsync(Row(rejected)));
        Assert.Contains($"already resubmitted as '{first.Id}'", refused.Message);
        Assert.Equal([first.Id], (await store.GetRecentAsync(100)).Select(r => r.Id));

        // Only a non-null SupersedesId is unique: requests that supersede nothing, and a rework of another rejection, add.
        await store.AddAsync(Row(null));
        await store.AddAsync(Row(null));
        await store.AddAsync(Row(Guid.NewGuid()));
        Assert.Equal(4, (await store.GetRecentAsync(100)).Count);
    }

    [Fact]
    public async Task An_add_refused_for_another_reason_is_not_reported_as_a_resubmission()
    {
        // The catch reads the rework back; with none there, the database's own error stands (a duplicate key here).
        var store = Store();
        var row = Row(Guid.NewGuid());
        await store.AddAsync(row);
        await store.AddAsync(Row(Guid.NewGuid()));   // a rework of another rejection: not this add's rework
        var sameId = new GoldpathApprovalRequest
        {
            Id = row.Id,
            Ladder = row.Ladder,
            Subject = row.Subject,
            RequestedBy = row.RequestedBy,
            PendingRole = row.PendingRole,
            SupersedesId = Guid.NewGuid(),
        };

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => store.AddAsync(sameId));
        Assert.DoesNotContain("already resubmitted", refused.Message);
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

    [Fact]
    public async Task A_resubmission_whose_trail_write_fails_leaves_nothing_and_its_retry_makes_one_rework_named_and_announced()
    {
        // #521, verifier on #589: the rework committed, the rejected request's superseded step did not, and every retry
        // was refused for good — a pending rework the rejected request never named and no event ever announced. On the
        // built-in store the two are one write, so the failure leaves neither and the retry starts clean.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var published = new Published();
        var engine = BuildEngine(clock, published);
        var rejected = await engine.RequestAsync("credit-limit", "K26-589", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing"));

        _failNextUpdate.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => engine.ResubmitAsync(rejected.Id, "maker"));
        Assert.False(_failNextUpdate.Armed);
        Assert.DoesNotContain(await Store().GetRecentAsync(100), r => r.SupersedesId == rejected.Id);
        Assert.Equal(["requested", "rejected"], (await Reload(rejected.Id)).Trail.Select(t => t.Action));

        var rework = await engine.ResubmitAsync(rejected.Id, "maker");

        Assert.Equal([rework.Id], (await Store().GetRecentAsync(100)).Where(r => r.SupersedesId == rejected.Id).Select(r => r.Id));
        var trail = (await Reload(rejected.Id)).Trail;
        Assert.Equal(["requested", "rejected", "superseded"], trail.Select(t => t.Action));
        Assert.Equal($"resubmitted as {rework.Id}", trail[^1].Detail);
        Assert.Equal([rework.Id], published.Events.OfType<GoldpathApprovalRequested>().Where(e => e.ApprovalId != rejected.Id).Select(e => e.ApprovalId));
    }

    [Fact]
    public async Task A_second_resubmission_in_the_first_ones_gap_is_refused_names_the_rework_and_writes_nothing()
    {
        // #521 on the database store, made deterministic: a whole resubmission lands between this one's read and its
        // write. The single write refuses on the unique SupersedesId or on the rejected request's version; either way
        // the refusal names the rework that landed, and nothing of this call is stored.
        var clock = new RacingClock(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var published = new Published();
        var engine = BuildEngine(clock, published);
        var rejected = await engine.RequestAsync("credit-limit", "K26-590", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "no"));
        GoldpathApprovalRequest? first = null;
        clock.OnNextRead = async () => first = await BuildEngine(new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z")), published).ResubmitAsync(rejected.Id, "maker");

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.ResubmitAsync(rejected.Id, "maker"));

        Assert.NotNull(first);
        Assert.Contains($"already resubmitted as '{first.Id}'", refused.Message);
        Assert.Equal([first.Id], (await Store().GetRecentAsync(100)).Where(r => r.SupersedesId == rejected.Id).Select(r => r.Id));
        var trail = (await Reload(rejected.Id)).Trail;
        Assert.Equal(["requested", "rejected", "superseded"], trail.Select(t => t.Action));
        Assert.Equal($"resubmitted as {first.Id}", trail[^1].Detail);
        Assert.Single(published.Events.OfType<GoldpathApprovalRequested>(), e => e.ApprovalId != rejected.Id);
    }

    [Fact]
    public async Task A_resubmission_that_loses_to_another_step_reads_again_and_writes_the_rework_and_its_step()
    {
        // The rejected request is terminal, so only another trail step can race the single write: it reads again and
        // puts the superseded step onto the request as it is now, with the rework.
        var clock = new RacingClock(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var engine = BuildEngine(clock);
        var rejected = await engine.RequestAsync("credit-limit", "K26-591", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "no"));
        clock.OnNextRead = async () =>
        {
            var current = await Reload(rejected.Id);
            current.Trail.Add(new GoldpathApprovalTrailEntry(DateTimeOffset.Parse("2026-11-03T09:00:00Z"), "auditor", "noted", "read"));
            await Store().UpdateAsync(current);
        };

        var rework = await engine.ResubmitAsync(rejected.Id, "maker");

        Assert.Equal([rework.Id], (await Store().GetRecentAsync(100)).Where(r => r.SupersedesId == rejected.Id).Select(r => r.Id));
        Assert.Equal(["requested", "rejected", "noted", "superseded"], (await Reload(rejected.Id)).Trail.Select(t => t.Action));
    }

    [Fact]
    public async Task A_resubmission_whose_single_write_loses_to_another_step_reads_again_and_writes_both()
    {
        // The write itself loses: another step lands on the rejected request after the engine's read, as the save
        // begins. The version check refuses the pair, and the retry reads the request as it is now.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var engine = BuildEngine(clock);
        var rejected = await engine.RequestAsync("credit-limit", "K26-594", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "no"));
        _beforeNextSave.Hook = async () =>
        {
            var current = await Reload(rejected.Id);
            current.Trail.Add(new GoldpathApprovalTrailEntry(DateTimeOffset.Parse("2026-11-03T09:00:00Z"), "auditor", "noted", "read"));
            await Store().UpdateAsync(current);
        };

        var rework = await engine.ResubmitAsync(rejected.Id, "maker");

        Assert.Null(_beforeNextSave.Hook);
        Assert.Equal([rework.Id], (await Store().GetRecentAsync(100)).Where(r => r.SupersedesId == rejected.Id).Select(r => r.Id));
        Assert.Equal(["requested", "rejected", "noted", "superseded"], (await Reload(rejected.Id)).Trail.Select(t => t.Action));
    }

    [Fact]
    public async Task A_rework_stored_without_its_step_refuses_a_resubmission_and_names_it()
    {
        // What the two-write path could leave in a database before this change: a rework whose rejected request has no
        // superseded step. The engine's read passes; the unique index refuses the pair and names the rework, so the
        // state is reported rather than doubled, and the rework it names is the one to decide.
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z"));
        var engine = BuildEngine(clock);
        var rejected = await engine.RequestAsync("credit-limit", "K26-595", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "no"));
        var orphan = Row(rejected.Id);
        await Store().AddAsync(orphan);

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.ResubmitAsync(rejected.Id, "maker"));

        Assert.Contains($"already resubmitted as '{orphan.Id}'", refused.Message);
        Assert.Equal([orphan.Id], (await Store().GetRecentAsync(100)).Where(r => r.SupersedesId == rejected.Id).Select(r => r.Id));
        Assert.Equal(["requested", "rejected"], (await Reload(rejected.Id)).Trail.Select(t => t.Action));
    }

    /// <summary>A pending request row that supersedes <paramref name="supersedes"/>, for the stores' own facts (#521).</summary>
    public static GoldpathApprovalRequest Row(Guid? supersedes) => new()
    {
        Id = Guid.NewGuid(),
        Ladder = "credit-limit",
        Subject = "K26-521",
        Amount = 500_000m,
        RequestedBy = "maker",
        RequestedAt = DateTimeOffset.Parse("2026-11-03T09:00:00Z"),
        PendingRole = "expert",
        PendingSince = DateTimeOffset.Parse("2026-11-03T09:00:00Z"),
        Status = GoldpathApprovalStatus.Pending,
        SupersedesId = supersedes,
    };

    private GoldpathEfApprovalStore<ApprovalsDbContext> Store()
        => new(_provider.GetRequiredService<IServiceScopeFactory>());

    private async Task<GoldpathApprovalRequest> Reload(Guid id)
    {
        var store = new GoldpathEfApprovalStore<ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        return await store.GetAsync(id) ?? throw new InvalidOperationException("missing request");
    }
}
