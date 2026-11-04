using System.Collections.Concurrent;
using Goldpath.Approvals.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Concurrent approval decisions on real PostgreSQL (#480, threat model T-APR-06). A decision reads the request and
/// its signatures, then writes; two decisions that both read before either writes used to both apply. Every save is
/// held back <see cref="Hold"/> here, so two decisions started together always both read first — the race is
/// forced, not hoped for. The rule: one identity signs a request once, and a request takes exactly one outcome
/// and publishes exactly one event for it.
/// </summary>
public sealed class ApprovalConcurrencyTests : IAsyncLifetime
{
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(300);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _provider = null!;

    /// <summary>Holds every save back, so concurrent decisions finish reading before any of them writes.</summary>
    private sealed class HoldEverySave : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Hold, cancellationToken);
            return result;
        }
    }

    /// <summary>Records every published event.</summary>
    private sealed class Published : IIntegrationEventPublisher
    {
        public ConcurrentQueue<IIntegrationEvent> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Events.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _provider = new ServiceCollection()
            .AddDbContext<EfApprovalStoreFacts.ApprovalsDbContext>(b => b
                .UseNpgsql(_postgres.GetConnectionString())
                .AddInterceptors(new HoldEverySave()))
            .BuildServiceProvider(true);
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EfApprovalStoreFacts.ApprovalsDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private (GoldpathApprovalEngine Engine, IGoldpathApprovalStore Store) Build(Published published)
    {
        var store = new GoldpathEfApprovalStore<EfApprovalStoreFacts.ApprovalsDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        var engine = new GoldpathApprovalEngine(
            new GoldpathApprovalsOptions().AddLadder("credit-limit", l => l
                .Rung("manager", 1_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
                .TopRung("general-manager", TimeSpan.FromHours(24))),
            store,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z")),
            NullLogger<GoldpathApprovalEngine>.Instance,
            published);
        return (engine, store);
    }

    [Fact]
    public async Task The_same_identity_granting_twice_at_once_signs_once_and_the_second_is_AlreadySigned()
    {
        var published = new Published();
        var (engine, store) = Build(published);
        var request = await engine.RequestAsync("credit-limit", "K26-480", 500_000m, "maker");

        // A double click, or two tabs: the same manager, twice, at the same instant.
        var outcomes = await Task.WhenAll(
            engine.DecideAsync(request.Id, "checker-a", "manager", granted: true, "ok"),
            engine.DecideAsync(request.Id, "checker-a", "manager", granted: true, "ok"));

        Assert.Equal(
            [GoldpathApprovalDecisionOutcome.Applied, GoldpathApprovalDecisionOutcome.AlreadySigned],
            outcomes.Order());
        var signatures = await store.GetSignaturesAsync(request.Id);
        Assert.Equal(["checker-a"], signatures.Select(s => s.SignedBy));

        // One person is one signature: the rung still needs a second, distinct one.
        var stored = await store.GetAsync(request.Id);
        Assert.Equal(GoldpathApprovalStatus.Pending, stored!.Status);
        Assert.Single(stored.Trail, t => t.Action == "signed");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(request.Id, "checker-b", "manager", granted: true, "ok"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await store.GetAsync(request.Id))!.Status);
    }

    [Fact]
    public async Task A_grant_and_a_reject_at_once_apply_exactly_one_outcome_and_publish_exactly_one_event()
    {
        var published = new Published();
        var (engine, store) = Build(published);
        // A one-signature rung, so a grant is terminal like a reject.
        var request = await engine.RequestAsync("credit-limit", "K26-481", 5_000_000m, "maker");
        Assert.Equal("general-manager", request.PendingRole);

        var outcomes = await Task.WhenAll(
            engine.DecideAsync(request.Id, "gm-a", "general-manager", granted: true, "fine"),
            engine.DecideAsync(request.Id, "gm-b", "general-manager", granted: false, "not fine"));

        Assert.Equal(
            [GoldpathApprovalDecisionOutcome.Applied, GoldpathApprovalDecisionOutcome.NotPending],
            outcomes.Order());
        var decisions = published.Events.Where(e => e is GoldpathApprovalGranted or GoldpathApprovalRejected).ToList();
        var stored = (await store.GetAsync(request.Id))!;
        var only = Assert.Single(decisions);

        // The one event says what the stored state says, and the trail holds one terminal step.
        Assert.Equal(only is GoldpathApprovalGranted ? GoldpathApprovalStatus.Granted : GoldpathApprovalStatus.Rejected, stored.Status);
        Assert.Single(stored.Trail, t => t.Action is "granted" or "rejected");
        Assert.Equal(only is GoldpathApprovalGranted ? 1 : 0, (await store.GetSignaturesAsync(request.Id)).Count);
    }

    [Fact]
    public async Task Two_distinct_signers_at_once_both_count_toward_the_quorum()
    {
        var published = new Published();
        var (engine, store) = Build(published);
        var request = await engine.RequestAsync("credit-limit", "K26-482", 500_000m, "maker");

        // Not a conflict to refuse: two different people, the two signatures the rung needs. The loser of the write
        // re-reads and signs on top of the winner, so the request is granted, once.
        var outcomes = await Task.WhenAll(
            engine.DecideAsync(request.Id, "checker-a", "manager", granted: true, "ok"),
            engine.DecideAsync(request.Id, "checker-b", "manager", granted: true, "ok"));

        Assert.All(outcomes, o => Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, o));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await store.GetAsync(request.Id))!.Status);
        Assert.Equal(["checker-a", "checker-b"], (await store.GetSignaturesAsync(request.Id)).Select(s => s.SignedBy).Order());
        Assert.Single(published.Events.OfType<GoldpathApprovalGranted>());
    }
}
