using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The v2 rules (RFC goldpath-approvals-v2) — the api-portal product's seven multi-stage
/// facts translated to rungs, plus withdraw and the reject→resubmit chain. Every fact here
/// shipped and ran live in the product before it landed in the package.
/// </summary>
public class ApprovalEngineV2Tests
{
    private static readonly TimeSpan RungDeadline = TimeSpan.FromHours(8);

    // "two managers" on the first rung: quorum is a rung property, not a second ladder.
    private static GoldpathApprovalsOptions Options() => new GoldpathApprovalsOptions()
        .AddLadder("payment-run", ladder => ladder
            .Rung("manager", 5_000_000m, RungDeadline, requiredApprovals: 2)
            .TopRung("general-manager", TimeSpan.FromHours(24)));

    private static (GoldpathApprovalEngine Engine, FakeTimeProvider Clock, RecordingPublisher Events, IGoldpathApprovalStore Store) Build()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-26T09:00:00Z"));
        var events = new RecordingPublisher();
        var store = new GoldpathInMemoryApprovalStore();
        var engine = new GoldpathApprovalEngine(
            Options(), store, clock,
            NullLogger<GoldpathApprovalEngine>.Instance, events);
        return (engine, clock, events, store);
    }

    [Fact]
    public async Task A_quorum_rung_stays_pending_until_the_required_distinct_grants_arrive()
    {
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-1", 1_000_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "manager-one", "manager", true, "first signature"));
        var afterFirst = await Reload(store, request.Id);
        Assert.Equal(GoldpathApprovalStatus.Pending, afterFirst.Status);
        Assert.Null(afterFirst.DecidedBy);
        Assert.DoesNotContain(events.Published, e => e is GoldpathApprovalGranted);

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "manager-two", "manager", true, "second signature"));
        var afterSecond = await Reload(store, request.Id);
        Assert.Equal(GoldpathApprovalStatus.Granted, afterSecond.Status);
        Assert.Equal("manager-two", afterSecond.DecidedBy);
        Assert.Contains(events.Published, e => e is GoldpathApprovalGranted g && g.DecidedBy == "manager-two");
    }

    [Fact]
    public async Task The_same_identity_may_not_sign_twice_on_one_rung()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-2", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "first");

        Assert.Equal(GoldpathApprovalDecisionOutcome.AlreadySigned,
            await engine.DecideAsync(request.Id, "manager-one", "manager", true, "again"));
        Assert.Equal(GoldpathApprovalStatus.Pending, (await Reload(store, request.Id)).Status);
    }

    [Fact]
    public async Task Distinct_eyes_holds_across_escalation_a_lower_rung_signer_may_not_sign_the_higher_rung()
    {
        var (engine, clock, _, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-3", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "shape-shifter", "manager", true, "signed at manager");

        clock.Advance(RungDeadline);
        Assert.Equal(1, await engine.EscalateOverdueAsync());
        Assert.Equal("general-manager", (await Reload(store, request.Id)).PendingRole);

        // Even holding the higher role, the earlier signer is barred for this request.
        Assert.Equal(GoldpathApprovalDecisionOutcome.AlreadySigned,
            await engine.DecideAsync(request.Id, "shape-shifter", "general-manager", true, "second bite"));
    }

    [Fact]
    public async Task An_escalated_rung_counts_its_own_quorum_from_zero()
    {
        var (engine, clock, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-4", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "one of two");

        clock.Advance(RungDeadline);
        await engine.EscalateOverdueAsync();

        // The top rung requires ONE grant; the manager-rung signature does not complete it,
        // but a fresh GM signature does.
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "the-gm", "general-manager", true, "gm sign-off"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await Reload(store, request.Id)).Status);
        Assert.Contains(events.Published, e => e is GoldpathApprovalGranted g && g.DecidedBy == "the-gm");
    }

    [Fact]
    public async Task Four_eyes_bars_the_requester_from_a_quorum_rung_too()
    {
        var (engine, _, _, _) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-5", 1_000_000m, "maker");
        Assert.Equal(GoldpathApprovalDecisionOutcome.FourEyesViolation,
            await engine.DecideAsync(request.Id, "maker", "manager", true, "self-serve"));
    }

    [Fact]
    public async Task A_rejection_mid_quorum_is_terminal()
    {
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-6", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "one of two");

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "manager-two", "manager", false, "duplicate invoice"));
        var reloaded = await Reload(store, request.Id);
        Assert.Equal(GoldpathApprovalStatus.Rejected, reloaded.Status);
        Assert.Equal("duplicate invoice", reloaded.Reason);
        Assert.Contains(events.Published, e => e is GoldpathApprovalRejected r && r.Reason == "duplicate invoice");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_rejection_without_a_reason_is_refused(string blank)
    {
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-7", 1_000_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.ReasonRequired,
            await engine.DecideAsync(request.Id, "manager-one", "manager", false, blank));
        Assert.Equal(GoldpathApprovalStatus.Pending, (await Reload(store, request.Id)).Status);
        Assert.DoesNotContain(events.Published, e => e is GoldpathApprovalRejected);
    }

    [Fact]
    public async Task The_trail_reads_signed_then_granted_with_the_quorum_count()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-8", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "first signature");
        await engine.DecideAsync(request.Id, "manager-two", "manager", true, "second signature");

        var trail = (await Reload(store, request.Id)).Trail;
        Assert.Equal(["requested", "signed", "granted"], trail.Select(t => t.Action));
        Assert.Equal("1/2 at manager: first signature", trail[1].Detail);
    }

    [Fact]
    public async Task Withdraw_takes_back_a_pending_request_and_lands_in_the_trail()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-9", 1_000_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.WithdrawAsync(request.Id, "maker"));
        var reloaded = await Reload(store, request.Id);
        Assert.Equal(GoldpathApprovalStatus.Withdrawn, reloaded.Status);
        Assert.Equal(["requested", "withdrawn"], reloaded.Trail.Select(t => t.Action));

        // Withdrawn is terminal: nobody can decide it afterwards.
        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending,
            await engine.DecideAsync(request.Id, "manager-one", "manager", true, "too late"));
    }

    [Fact]
    public async Task Only_the_requester_may_withdraw()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-10", 1_000_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.NotRequester,
            await engine.WithdrawAsync(request.Id, "someone-else"));
        Assert.Equal(GoldpathApprovalStatus.Pending, (await Reload(store, request.Id)).Status);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_withdrawn()
    {
        var (engine, _, _, _) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-11", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", false, "declined");

        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending,
            await engine.WithdrawAsync(request.Id, "maker"));
        Assert.Equal(GoldpathApprovalDecisionOutcome.NotFound,
            await engine.WithdrawAsync(Guid.NewGuid(), "maker"));
    }

    [Fact]
    public async Task Resubmit_links_the_chain_and_routes_the_fresh_request()
    {
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-12", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", false, "wrong cost center");

        var resubmitted = await engine.ResubmitAsync(request.Id, "maker");

        Assert.Equal(GoldpathApprovalStatus.Pending, resubmitted.Status);
        Assert.Equal(request.Id, resubmitted.SupersedesId);
        Assert.Equal("manager", resubmitted.PendingRole);
        Assert.Equal(request.Amount, resubmitted.Amount);
        Assert.Equal(["resubmitted"], resubmitted.Trail.Select(t => t.Action));

        // Both trails tell the one story: the old request records what superseded it.
        var oldTrail = (await Reload(store, request.Id)).Trail;
        Assert.Equal(["requested", "rejected", "superseded"], oldTrail.Select(t => t.Action));
        Assert.Contains(resubmitted.Id.ToString(), oldTrail[^1].Detail);

        Assert.Contains(events.Published, e => e is GoldpathApprovalRequested r && r.ApprovalId == resubmitted.Id);
    }

    [Fact]
    public async Task A_rejected_request_is_resubmitted_once_the_second_call_is_refused_and_names_the_rework()
    {
        // #521: a double click, or a retry after a timeout, used to create a second rework of the same rejection.
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("payment-run", "P26-21", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", false, "wrong cost center");
        var first = await engine.ResubmitAsync(request.Id, "maker");

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.ResubmitAsync(request.Id, "maker"));

        Assert.Equal(
            $"Request '{request.Id}' was already resubmitted as '{first.Id}' — a rejected request is resubmitted once; decide on the rework instead.",
            refused.Message);
        Assert.Equal([first.Id], (await store.GetRecentAsync(100)).Where(r => r.SupersedesId == request.Id).Select(r => r.Id));
        Assert.Equal(["requested", "rejected", "superseded"], (await Reload(store, request.Id)).Trail.Select(t => t.Action));
        Assert.Single(events.Published, e => e is GoldpathApprovalRequested r && r.ApprovalId != request.Id);
    }

    [Fact]
    public async Task On_a_custom_store_with_no_rule_of_its_own_the_trail_still_refuses_a_second_resubmission()
    {
        // A store outside the module keeps its own guarantees for two calls AT ONCE; one after the other, the engine's
        // read of the superseded step holds on any store.
        var store = new NoRulesStore();
        var engine = new GoldpathApprovalEngine(Options(), store, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z")),
            NullLogger<GoldpathApprovalEngine>.Instance);
        var request = await engine.RequestAsync("payment-run", "P26-22", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", false, "wrong cost center");
        var first = await engine.ResubmitAsync(request.Id, "maker");

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.ResubmitAsync(request.Id, "maker"));

        Assert.Contains($"already resubmitted as '{first.Id}'", refused.Message);
        Assert.Equal([first.Id], store.Requests.Values.Where(r => r.SupersedesId == request.Id).Select(r => r.Id));
    }

    [Fact]
    public async Task A_superseded_step_whose_detail_names_no_rework_still_refuses_and_says_so_without_an_id()
    {
        // The step is the refusal, whatever its detail says: a trail edited outside the engine refuses all the same.
        var store = new NoRulesStore();
        var engine = new GoldpathApprovalEngine(Options(), store, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-03T09:00:00Z")),
            NullLogger<GoldpathApprovalEngine>.Instance);
        var request = await engine.RequestAsync("payment-run", "P26-23", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", false, "wrong cost center");
        store.Requests[request.Id].Trail.Add(new GoldpathApprovalTrailEntry(DateTimeOffset.Parse("2026-11-03T09:00:00Z"), "maker", "superseded", "resubmitted as not-a-guid"));

        var refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => engine.ResubmitAsync(request.Id, "maker"));

        Assert.Equal(
            $"Request '{request.Id}' was already resubmitted — a rejected request is resubmitted once; decide on the rework instead.",
            refused.Message);
        Assert.DoesNotContain(store.Requests.Values, r => r.SupersedesId == request.Id);
    }

    /// <summary>A store that refuses nothing: no version check, no unique key — what a naive adopter store looks like.</summary>
    private sealed class NoRulesStore : IGoldpathApprovalStore
    {
        public Dictionary<Guid, GoldpathApprovalRequest> Requests { get; } = [];

        private readonly List<GoldpathApprovalSignature> _signatures = [];

        private static GoldpathApprovalRequest Copy(GoldpathApprovalRequest r)
        {
            var copy = new GoldpathApprovalRequest
            {
                Id = r.Id,
                Ladder = r.Ladder,
                Subject = r.Subject,
                Amount = r.Amount,
                RequestedBy = r.RequestedBy,
                RequestedAt = r.RequestedAt,
                PendingRole = r.PendingRole,
                PendingSince = r.PendingSince,
                Status = r.Status,
                DecidedBy = r.DecidedBy,
                Reason = r.Reason,
                SupersedesId = r.SupersedesId,
            };
            copy.Trail.AddRange(r.Trail);
            return copy;
        }

        public Task AddAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests[request.Id] = Copy(request);
            return Task.CompletedTask;
        }

        public Task<GoldpathApprovalRequest?> GetAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(Requests.TryGetValue(id, out var r) ? Copy(r) : null);

        public Task UpdateAsync(GoldpathApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests[request.Id] = Copy(request);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GoldpathApprovalRequest>> GetPendingAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GoldpathApprovalRequest>>(Requests.Values.Where(r => r.Status == GoldpathApprovalStatus.Pending).Select(r => Copy(r)).ToList());

        public Task<IReadOnlyList<GoldpathApprovalRequest>> GetRecentAsync(int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GoldpathApprovalRequest>>(Requests.Values.Take(take).Select(r => Copy(r)).ToList());

        public Task AddDelegationAsync(GoldpathApprovalDelegation delegation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GoldpathApprovalDelegation>> GetDelegationsAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GoldpathApprovalDelegation>>([]);

        public Task AddSignatureAsync(GoldpathApprovalSignature signature, CancellationToken cancellationToken = default)
        {
            _signatures.Add(signature);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GoldpathApprovalSignature>> GetSignaturesAsync(Guid requestId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GoldpathApprovalSignature>>(_signatures.Where(s => s.RequestId == requestId).ToList());
    }

    [Fact]
    public async Task Only_a_rejected_request_may_be_resubmitted()
    {
        var (engine, _, _, _) = Build();
        var pending = await engine.RequestAsync("payment-run", "P26-13", 1_000_000m, "maker");

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResubmitAsync(pending.Id, "maker"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResubmitAsync(Guid.NewGuid(), "maker"));
    }

    [Fact]
    public void A_rung_quorum_below_one_is_rejected_at_declaration()
    {
        Assert.Throws<InvalidOperationException>(() => new GoldpathApprovalsOptions()
            .AddLadder("broken", l => l
                .Rung("manager", 1_000_000m, RungDeadline, requiredApprovals: 0)
                .TopRung("gm", RungDeadline)));
    }

    private static async Task<GoldpathApprovalRequest> Reload(IGoldpathApprovalStore store, Guid id)
        => await store.GetAsync(id) ?? throw new InvalidOperationException($"request {id} not in the store");

    private sealed class RecordingPublisher : IIntegrationEventPublisher
    {
        public List<IIntegrationEvent> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Published.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }
}
