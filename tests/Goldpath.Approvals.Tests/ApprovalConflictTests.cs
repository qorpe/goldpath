using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// What the engine does when its write loses a race (#480), made deterministic: <see cref="RacingStore"/> lands
/// another write on the request between the engine's read and its write. A decision reads again and decides on what
/// the request is now; the escalation sweep leaves a request it lost to the decision that won; resubmission puts its
/// step on the request as it is now; and a call that loses every time stops instead of spinning.
/// </summary>
public sealed class ApprovalConflictTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T09:00:00Z");

    /// <summary>The in-memory store, with a hook that runs just before each request write the engine makes.</summary>
    private sealed class RacingStore(GoldpathInMemoryApprovalStore inner) : IGoldpathApprovalStore
    {
        public Func<Task>? BeforeWrite { get; set; }

        public bool EveryWrite { get; set; }

        private async Task RaceAsync()
        {
            var hook = BeforeWrite;
            if (!EveryWrite)
            {
                BeforeWrite = null;
            }

            if (hook is not null)
            {
                await hook();
            }
        }

        public Task AddAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default) => inner.AddAsync(request, cancellationToken);
        public Task<GoldpathApprovalRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);

        public async Task UpdateAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
        {
            await RaceAsync();
            await inner.UpdateAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<GoldpathApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default) => inner.GetPendingAsync(cancellationToken);
        public Task<IReadOnlyList<GoldpathApprovalRequest>> GetRecentAsync(int take, CancellationToken cancellationToken = default) => inner.GetRecentAsync(take, cancellationToken);
        public Task AddDelegationAsync(GoldpathApprovalDelegation delegation, CancellationToken cancellationToken = default) => inner.AddDelegationAsync(delegation, cancellationToken);
        public Task<IReadOnlyList<GoldpathApprovalDelegation>> GetDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.GetDelegationsAsync(now, cancellationToken);
        public Task AddSignatureAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken = default) => inner.AddSignatureAsync(signature, cancellationToken);
        public Task<IReadOnlyList<GoldpathApprovalSignature>> GetSignaturesAsync(Guid requestId, CancellationToken cancellationToken = default) => inner.GetSignaturesAsync(requestId, cancellationToken);
    }

    private sealed class Published : IIntegrationEventPublisher
    {
        public List<IIntegrationEvent> Events { get; } = [];

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private static GoldpathApprovalEngine Engine(IGoldpathApprovalStore store, TimeProvider clock, Published? published = null) => new(
        new GoldpathApprovalsOptions().AddLadder("credit-limit", l => l
            .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
            .TopRung("general-manager", TimeSpan.FromHours(24))),
        store, clock, NullLogger<GoldpathApprovalEngine>.Instance, published);

    [Fact]
    public async Task A_reject_that_loses_to_a_grant_reads_again_and_answers_NotPending_without_an_event()
    {
        var inner = new GoldpathInMemoryApprovalStore();
        var clock = new FakeTimeProvider(At);
        var racing = new RacingStore(inner);
        var published = new Published();
        var engine = Engine(racing, clock, published);
        var request = await engine.RequestAsync("credit-limit", "K26-480", 500_000m, "maker");

        // Between the reject's read and its write, another checker grants on the same store.
        racing.BeforeWrite = async () => Assert.Equal(
            GoldpathApprovalDecisionOutcome.Applied,
            await Engine(inner, clock).DecideAsync(request.Id, "checker-a", "expert", true, "fine"));

        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending, await engine.DecideAsync(request.Id, "checker-b", "expert", false, "not fine"));
        var stored = (await inner.GetAsync(request.Id))!;
        Assert.Equal(GoldpathApprovalStatus.Granted, stored.Status);
        Assert.Equal(["requested", "granted"], stored.Trail.Select(t => t.Action));
        Assert.DoesNotContain(published.Events, e => e is GoldpathApprovalRejected);
    }

    [Fact]
    public async Task The_sweep_leaves_a_request_decided_under_it_to_that_decision()
    {
        var inner = new GoldpathInMemoryApprovalStore();
        var clock = new FakeTimeProvider(At);
        var racing = new RacingStore(inner);
        var published = new Published();
        var engine = Engine(racing, clock, published);
        var request = await engine.RequestAsync("credit-limit", "K26-481", 500_000m, "maker");
        clock.Advance(TimeSpan.FromHours(9));   // past the expert rung's deadline

        racing.BeforeWrite = () => Engine(inner, clock).DecideAsync(request.Id, "checker-a", "expert", false, "no");

        Assert.Equal(0, await engine.EscalateOverdueAsync());
        var stored = (await inner.GetAsync(request.Id))!;
        Assert.Equal(GoldpathApprovalStatus.Rejected, stored.Status);
        Assert.Equal("expert", stored.PendingRole);
        Assert.DoesNotContain(published.Events, e => e is GoldpathApprovalEscalated);

        // The next sweep reads it again: it is no longer pending, so nothing moves.
        Assert.Equal(0, await engine.EscalateOverdueAsync());
    }

    [Fact]
    public async Task Resubmission_puts_its_step_on_the_rejected_request_as_it_is_now()
    {
        var inner = new GoldpathInMemoryApprovalStore();
        var clock = new FakeTimeProvider(At);
        var racing = new RacingStore(inner);
        var engine = Engine(racing, clock);
        var rejected = await engine.RequestAsync("credit-limit", "K26-482", 500_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing"));

        // A second resubmission of the same rejected request lands its step first.
        racing.BeforeWrite = async () => await Engine(inner, clock).ResubmitAsync(rejected.Id, "maker");

        var resubmitted = await engine.ResubmitAsync(rejected.Id, "maker");
        var trail = (await inner.GetAsync(rejected.Id))!.Trail;
        Assert.Equal(["requested", "rejected", "superseded", "superseded"], trail.Select(t => t.Action));
        Assert.Contains(trail, t => t.Detail == $"resubmitted as {resubmitted.Id}");
    }

    [Fact]
    public async Task On_the_in_memory_store_a_grant_that_loses_to_a_reject_answers_NotPending_and_signs_nothing()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var clock = new EfApprovalStoreFacts.RacingClock(At);
        var published = new Published();
        var engine = Engine(store, clock, published);
        var request = await engine.RequestAsync("credit-limit", "K26-484", 500_000m, "maker");

        // The grant has read the request; the reject lands before the grant writes its signature and the request.
        clock.OnNextRead = async () => Assert.Equal(
            GoldpathApprovalDecisionOutcome.Applied,
            await Engine(store, new FakeTimeProvider(At)).DecideAsync(request.Id, "checker-b", "expert", false, "no"));

        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending, await engine.DecideAsync(request.Id, "checker-a", "expert", true, "yes"));
        Assert.Empty(await store.GetSignaturesAsync(request.Id));
        Assert.Equal(GoldpathApprovalStatus.Rejected, (await store.GetAsync(request.Id))!.Status);
        Assert.DoesNotContain(published.Events, e => e is GoldpathApprovalGranted);
    }

    [Fact]
    public async Task A_custom_store_still_gets_the_signature_and_the_request_as_two_writes()
    {
        // A store outside the module keeps its own guarantees: the engine writes to it as it always has.
        var inner = new GoldpathInMemoryApprovalStore();
        var clock = new FakeTimeProvider(At);
        var published = new Published();
        var engine = Engine(new RacingStore(inner), clock, published);
        var request = await engine.RequestAsync("credit-limit", "K26-485", 500_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(request.Id, "checker", "expert", true, "ok"));
        Assert.Equal(["checker"], (await inner.GetSignaturesAsync(request.Id)).Select(s => s.SignedBy));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await inner.GetAsync(request.Id))!.Status);
        Assert.Single(published.Events.OfType<GoldpathApprovalGranted>());
    }

    [Fact]
    public async Task A_decision_that_loses_every_time_stops_with_an_InvalidOperationException()
    {
        var inner = new GoldpathInMemoryApprovalStore();
        var clock = new FakeTimeProvider(At);
        var racing = new RacingStore(inner) { EveryWrite = true };
        var engine = Engine(racing, clock);
        var request = await engine.RequestAsync("credit-limit", "K26-483", 500_000m, "maker");

        var writes = 0;
        racing.BeforeWrite = async () =>
        {
            writes++;
            var current = (await inner.GetAsync(request.Id))!;
            current.Trail.Add(new GoldpathApprovalTrailEntry(At, "someone", "noted", $"{writes}"));
            await inner.UpdateAsync(current);
        };

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => engine.DecideAsync(request.Id, "checker", "expert", false, "no"));
        Assert.Equal(8, writes);
        Assert.Equal(GoldpathApprovalStatus.Pending, (await inner.GetAsync(request.Id))!.Status);
    }
}
