using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

public class ApprovalEngineTests
{
    private static readonly TimeSpan RungDeadline = TimeSpan.FromHours(8);

    private static GoldpathApprovalsOptions Options() => new GoldpathApprovalsOptions()
        .AddLadder("credit-limit", ladder => ladder
            .Rung("expert", 1_000_000m, RungDeadline)
            .Rung("deputy-manager", 5_000_000m, RungDeadline)
            .Rung("manager", 15_000_000m, RungDeadline)
            .TopRung("general-manager", TimeSpan.FromHours(24)));

    private static (GoldpathApprovalEngine Engine, FakeTimeProvider Clock, RecordingPublisher Events, IGoldpathApprovalStore Store) Build()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var events = new RecordingPublisher();
        var store = new GoldpathInMemoryApprovalStore();
        var engine = new GoldpathApprovalEngine(
            Options(), store, clock,
            NullLogger<GoldpathApprovalEngine>.Instance, events);
        return (engine, clock, events, store);
    }

    [Theory]
    [InlineData(1_000_000, "expert")]           // inclusive ceiling stays on the rung
    [InlineData(1_000_000.01, "deputy-manager")] // one cent over crosses it
    [InlineData(5_000_000, "deputy-manager")]
    [InlineData(15_000_000, "manager")]
    [InlineData(15_000_000.01, "general-manager")]
    [InlineData(999_999_999, "general-manager")] // above every ceiling routes to the top
    public async Task Amount_routes_the_rung_boundaries_inclusive(decimal amount, string expectedRole)
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-1", amount, "maker");
        Assert.Equal(expectedRole, request.PendingRole);
    }

    [Fact]
    public async Task Four_eyes_the_requester_may_never_decide_their_own_request()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-2", 500_000m, "maker");
        var outcome = await engine.DecideAsync(request.Id, "maker", "expert", granted: true, "self-serve attempt");
        Assert.Equal(GoldpathApprovalDecisionOutcome.FourEyesViolation, outcome);
        Assert.Equal(GoldpathApprovalStatus.Pending, (await Reload(store, request.Id)).Status);
    }

    [Fact]
    public async Task Wrong_role_is_refused_and_right_role_is_applied()
    {
        var (engine, _, events, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-3", 500_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.WrongRole,
            await engine.DecideAsync(request.Id, "checker", "manager", true, "wrong rung"));

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "checker", "expert", true, "fits the limit"));
        Assert.Contains(events.Published, e => e is GoldpathApprovalGranted g && g.ApprovalId == request.Id);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-4", 500_000m, "maker");
        await engine.DecideAsync(request.Id, "checker", "expert", false, "insufficient collateral");
        Assert.Equal(GoldpathApprovalDecisionOutcome.NotPending,
            await engine.DecideAsync(request.Id, "other", "expert", true, "second opinion"));
    }

    [Fact]
    public async Task Escalation_moves_one_rung_on_deadline_and_resets_the_clock()
    {
        var (engine, clock, events, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-5", 500_000m, "maker");

        clock.Advance(RungDeadline - TimeSpan.FromMinutes(1));
        Assert.Equal(0, await engine.EscalateOverdueAsync());   // not overdue yet

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await engine.EscalateOverdueAsync());
        var reloaded = await Reload(store, request.Id);
        Assert.Equal("deputy-manager", reloaded.PendingRole);
        Assert.Contains(events.Published, e => e is GoldpathApprovalEscalated m && m.ToRole == "deputy-manager");

        // The clock reset: the next rung gets its OWN full deadline.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await engine.EscalateOverdueAsync());
    }

    [Fact]
    public async Task Overdue_at_the_top_rung_expires_not_loops()
    {
        var (engine, clock, events, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-6", 99_000_000m, "maker");
        Assert.Equal("general-manager", request.PendingRole);

        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(1, await engine.EscalateOverdueAsync());
        Assert.Equal(GoldpathApprovalStatus.Expired, (await Reload(store, request.Id)).Status);
        Assert.Contains(events.Published, e => e is GoldpathApprovalExpired);
    }

    [Fact]
    public async Task Delegation_admits_the_delegate_and_depth_is_one()
    {
        var (engine, _, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-7", 500_000m, "maker");

        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(3));
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(request.Id, "stand-in", "no-role", true, "delegated decision"));

        // The stand-in holds a delegation — re-delegating would be a chain; refused.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DelegateRoleAsync("stand-in", "expert", "third", TimeSpan.FromDays(1)));
    }

    [Fact]
    public async Task A_delegate_of_a_lower_rung_is_refused_at_a_higher_rung()
    {
        // #479 (T-APR-05): an expert's stand-in decides the expert rung, never the general manager's.
        var (engine, _, _, store) = Build();
        var expertRung = await engine.RequestAsync("credit-limit", "K26-7a", 500_000m, "maker");
        var topRung = await engine.RequestAsync("credit-limit", "K26-7b", 99_000_000m, "maker");
        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(3));

        Assert.Equal(GoldpathApprovalDecisionOutcome.WrongRole,
            await engine.DecideAsync(topRung.Id, "stand-in", "no-role", true, "beyond the delegated rung"));
        Assert.Equal(GoldpathApprovalStatus.Pending, (await Reload(store, topRung.Id)).Status);
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(expertRung.Id, "stand-in", "no-role", true, "the delegated rung"));
    }

    [Fact]
    public async Task A_delegation_admits_its_role_however_the_role_is_cased_and_on_every_ladder_with_that_rung()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-18T09:00:00Z"));
        var options = Options().AddLadder("overdraft", ladder => ladder
            .Rung("expert", 10_000m, RungDeadline)
            .TopRung("general-manager", TimeSpan.FromHours(24)));
        var store = new GoldpathInMemoryApprovalStore();
        var engine = new GoldpathApprovalEngine(options, store, clock,
            NullLogger<GoldpathApprovalEngine>.Instance, new RecordingPublisher());
        var overdraft = await engine.RequestAsync("overdraft", "OD-1", 5_000m, "maker");
        await engine.DelegateRoleAsync("expert-user", "EXPERT", "stand-in", TimeSpan.FromDays(1));
        Assert.Equal("expert", Assert.Single(await store.GetDelegationsAsync(clock.GetUtcNow())).Role);   // the ladder's spelling

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(overdraft.Id, "STAND-IN", "no-role", true, "same rung, another ladder"));
    }

    [Fact]
    public async Task The_worklist_shows_a_delegate_only_the_delegated_rung()
    {
        var (engine, _, _, _) = Build();
        var expertRung = await engine.RequestAsync("credit-limit", "K26-7c", 500_000m, "maker");
        await engine.RequestAsync("credit-limit", "K26-7d", 9_000_000m, "maker");     // manager rung
        await engine.RequestAsync("credit-limit", "K26-7e", 99_000_000m, "maker");    // general-manager rung
        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(3));

        var worklist = await engine.WorklistAsync("stand-in", "no-role");

        Assert.Equal(expertRung.Id, Assert.Single(worklist).Id);
    }

    [Fact]
    public async Task A_delegation_recorded_without_a_role_admits_its_delegate_to_nothing()
    {
        // A row written before preview.10 has no role; it must not keep the old identity-level reach.
        var (engine, clock, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-7f", 500_000m, "maker");
        await store.AddDelegationAsync(new GoldpathApprovalDelegation("expert-user", "stand-in", clock.GetUtcNow() + TimeSpan.FromDays(1)));

        Assert.Equal(GoldpathApprovalDecisionOutcome.WrongRole,
            await engine.DecideAsync(request.Id, "stand-in", "no-role", true, "role-less delegation"));
        Assert.Empty(await engine.WorklistAsync("stand-in", "no-role"));
    }

    [Fact]
    public async Task The_role_less_delegate_verb_refuses_with_teaching_text_and_records_nothing()
    {
        var (engine, clock, _, store) = Build();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DelegateAsync("expert-user", "stand-in", TimeSpan.FromDays(1)));

        Assert.Contains("#479", refused.Message, StringComparison.Ordinal);
        Assert.Contains("DelegateRoleAsync(from, fromRole, to, window)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("never from a request body", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await store.GetDelegationsAsync(clock.GetUtcNow()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auditor")]       // no rung of any declared ladder
    public async Task A_delegation_of_a_role_that_decides_nothing_is_refused(string role)
    {
        var (engine, clock, _, store) = Build();

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => engine.DelegateRoleAsync("expert-user", role, "stand-in", TimeSpan.FromDays(1)));

        Assert.Equal("fromRole", refused.ParamName);
        Assert.StartsWith($"'{role}' is no rung of any declared ladder, so a delegation of it would admit to nothing.", refused.Message, StringComparison.Ordinal);
        Assert.Empty(await store.GetDelegationsAsync(clock.GetUtcNow()));
    }

    [Fact]
    public async Task Expired_delegation_admits_nobody()
    {
        var (engine, clock, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-8", 500_000m, "maker");
        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(3));
        clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromSeconds(1));
        Assert.Equal(GoldpathApprovalDecisionOutcome.WrongRole,
            await engine.DecideAsync(request.Id, "stand-in", "no-role", true, "too late"));
    }

    [Fact]
    public async Task Worklist_is_role_scoped_four_eyes_filtered_and_oldest_first()
    {
        var (engine, clock, _, store) = Build();
        var first = await engine.RequestAsync("credit-limit", "K26-9", 500_000m, "maker");
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await engine.RequestAsync("credit-limit", "K26-10", 700_000m, "checker");
        await engine.RequestAsync("credit-limit", "K26-11", 9_000_000m, "maker");   // manager rung

        var checkerList = await engine.WorklistAsync("checker", "expert");
        Assert.Equal([first.Id], checkerList.Select(r => r.Id));   // own request filtered, manager rung excluded

        var expertList = await engine.WorklistAsync("someone-else", "expert");
        Assert.Equal([first.Id, second.Id], expertList.Select(r => r.Id));   // oldest first
    }

    [Fact]
    public async Task The_trail_records_every_lifecycle_step()
    {
        var (engine, clock, _, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-12", 500_000m, "maker");
        clock.Advance(RungDeadline);
        await engine.EscalateOverdueAsync();
        await engine.DecideAsync(request.Id, "deputy", "deputy-manager", true, "approved on review");

        var trail = (await Reload(store, request.Id)).Trail;
        Assert.Equal(["requested", "escalated", "granted"], trail.Select(t => t.Action));
    }

    [Fact]
    public void A_ladder_without_a_top_rung_is_rejected_at_declaration()
    {
        Assert.Throws<InvalidOperationException>(() => new GoldpathApprovalsOptions()
            .AddLadder("broken", l => l.Rung("expert", 1_000_000m, RungDeadline)));
    }

    [Fact]
    public void Non_increasing_ceilings_are_rejected_at_declaration()
    {
        Assert.Throws<InvalidOperationException>(() => new GoldpathApprovalsOptions()
            .AddLadder("broken", l => l
                .Rung("expert", 5_000_000m, RungDeadline)
                .Rung("deputy", 1_000_000m, RungDeadline)
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
