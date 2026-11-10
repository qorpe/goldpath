using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The details an auditor and an operator actually read: trail wording, refusal messages,
/// and the engine's behavior when configuration drifted under a live request. Pinned
/// because they are the module's HUMAN surface — a mutated message is a broken feature.
/// </summary>
public class ApprovalEngineDetailTests
{
    private static readonly TimeSpan RungDeadline = TimeSpan.FromHours(8);

    private static GoldpathApprovalsOptions Options() => new GoldpathApprovalsOptions()
        .AddLadder("credit-limit", ladder => ladder
            .Rung("expert", 1_000_000m, RungDeadline)
            .TopRung("general-manager", TimeSpan.FromHours(24)));

    private static (GoldpathApprovalEngine Engine, FakeTimeProvider Clock, GoldpathInMemoryApprovalStore Store) Build(GoldpathApprovalsOptions? options = null)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-26T09:00:00Z"));
        var store = new GoldpathInMemoryApprovalStore();
        var engine = new GoldpathApprovalEngine(
            options ?? Options(), store, clock,
            NullLogger<GoldpathApprovalEngine>.Instance);
        return (engine, clock, store);
    }

    [Fact]
    public async Task The_trail_wording_is_the_audit_surface()
    {
        var (engine, clock, store) = Build();
        var request = await engine.RequestAsync("credit-limit", "K26-300", 500_000m, "maker");
        Assert.Equal("routed to expert for 500000", request.Trail[0].Detail);
        Assert.Equal("maker", request.Trail[0].Actor);

        clock.Advance(RungDeadline);
        await engine.EscalateOverdueAsync();
        var escalated = (await Reload(store, request.Id)).Trail[^1];
        Assert.Equal("expert -> general-manager", escalated.Detail);
        Assert.Equal("system", escalated.Actor);

        clock.Advance(TimeSpan.FromHours(24));
        await engine.EscalateOverdueAsync();
        var expired = (await Reload(store, request.Id)).Trail[^1];
        Assert.Equal("overdue at top rung general-manager", expired.Detail);
    }

    [Fact]
    public async Task Withdraw_and_resubmit_trail_wording()
    {
        var (engine, _, store) = Build();
        var withdrawn = await engine.RequestAsync("credit-limit", "K26-301", 500_000m, "maker");
        await engine.WithdrawAsync(withdrawn.Id, "maker");
        Assert.Equal("taken back by the requester", (await Reload(store, withdrawn.Id)).Trail[^1].Detail);

        var rejected = await engine.RequestAsync("credit-limit", "K26-302", 500_000m, "maker");
        await engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing");
        var resubmitted = await engine.ResubmitAsync(rejected.Id, "maker");
        Assert.Equal($"supersedes {rejected.Id}; routed to expert for 500000", resubmitted.Trail[0].Detail);
        Assert.Equal($"resubmitted as {resubmitted.Id}", (await Reload(store, rejected.Id)).Trail[^1].Detail);
    }

    [Fact]
    public async Task Refusal_messages_name_the_rule_that_refused()
    {
        var (engine, _, _) = Build();
        var undeclared = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RequestAsync("ghost-ladder", "K26-303", 1m, "maker"));
        Assert.Contains("'ghost-ladder' is not declared", undeclared.Message);

        var pending = await engine.RequestAsync("credit-limit", "K26-304", 500_000m, "maker");
        var notRejected = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ResubmitAsync(pending.Id, "maker"));
        Assert.Contains($"'{pending.Id}' is Pending", notRejected.Message);

        var missing = Guid.NewGuid();
        var notFound = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ResubmitAsync(missing, "maker"));
        Assert.Contains($"'{missing}' does not exist", notFound.Message);

        var self = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DelegateRoleAsync("expert-user", "expert", "expert-user", TimeSpan.FromDays(1)));
        Assert.Contains("Delegation to self", self.Message);

        var window = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(15)));
        Assert.Contains("exceeds the declared maximum", window.Message);

        await engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromDays(1));
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.DelegateRoleAsync("stand-in", "expert", "third", TimeSpan.FromDays(1)));
        Assert.Contains("depth is one", depth.Message);
    }

    [Theory]
    [InlineData("no-rungs")]
    [InlineData("no-top")]
    [InlineData("ceilings")]
    [InlineData("quorum")]
    public void Declaration_errors_name_the_broken_ladder_and_the_rule(string kind)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new GoldpathApprovalsOptions()
            .AddLadder("broken", l =>
            {
                switch (kind)
                {
                    case "no-top":
                        l.Rung("expert", 1_000_000m, RungDeadline);
                        break;
                    case "ceilings":
                        l.Rung("expert", 5_000_000m, RungDeadline).Rung("deputy", 5_000_000m, RungDeadline).TopRung("gm", RungDeadline);
                        break;
                    case "quorum":
                        l.Rung("expert", 1_000_000m, RungDeadline, requiredApprovals: 0).TopRung("gm", RungDeadline);
                        break;
                }
            }));
        Assert.Contains("'broken'", ex.Message);
        Assert.Contains(kind switch
        {
            "no-rungs" => "declares no rungs",
            "no-top" => "has no TopRung",
            "ceilings" => "must strictly increase",
            _ => "quorum below one",
        }, ex.Message);
    }

    [Fact]
    public async Task A_request_whose_ladder_was_undeclared_after_the_fact_still_decides_with_a_quorum_of_one()
    {
        // Configuration drift: the app dropped the ladder while a request was pending.
        // Deciding still works (quorum falls back to one); escalation skips it rather than
        // guessing a deadline.
        var (engine, _, store) = Build();
        var orphan = new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = "retired-ladder",
            Subject = "K26-305",
            Amount = 1m,
            RequestedBy = "maker",
            PendingRole = "expert",
            Status = GoldpathApprovalStatus.Pending,
        };
        await store.AddAsync(orphan);

        Assert.Equal(0, await engine.EscalateOverdueAsync());
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(orphan.Id, "checker", "expert", true, "fits"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await Reload(store, orphan.Id)).Status);
    }

    [Fact]
    public async Task A_request_pending_on_a_role_the_ladder_no_longer_declares_falls_back_to_a_quorum_of_one()
    {
        var (engine, _, store) = Build();
        var orphan = new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = "credit-limit",
            Subject = "K26-306",
            Amount = 1m,
            RequestedBy = "maker",
            PendingRole = "retired-role",
            Status = GoldpathApprovalStatus.Pending,
        };
        await store.AddAsync(orphan);

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied,
            await engine.DecideAsync(orphan.Id, "checker", "retired-role", true, "fits"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await Reload(store, orphan.Id)).Status);
    }

    [Fact]
    public async Task In_memory_signatures_come_back_oldest_first_and_scoped_to_the_request()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var requestId = Guid.NewGuid();
        var later = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var earlier = DateTimeOffset.Parse("2026-08-26T09:00:00Z");
        await store.AddSignatureAsync(new GoldpathApprovalSignature(requestId, "manager-two", "manager", later));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(requestId, "manager-one", "manager", earlier));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(Guid.NewGuid(), "stranger", "manager", earlier));

        Assert.Equal(["manager-one", "manager-two"],
            (await store.GetSignaturesAsync(requestId)).Select(s => s.SignedBy));
    }

    [Fact]
    public async Task An_in_memory_delegation_expiring_exactly_now_admits_nobody()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var now = DateTimeOffset.Parse("2026-08-26T09:00:00Z");
        await store.AddDelegationAsync(new GoldpathApprovalDelegation("expert-user", "stand-in", now));

        Assert.Empty(await store.GetDelegationsAsync(now));   // Until > now is strict
        Assert.Single(await store.GetDelegationsAsync(now - TimeSpan.FromSeconds(1)));
    }

    private static async Task<GoldpathApprovalRequest> Reload(IGoldpathApprovalStore store, Guid id)
        => await store.GetAsync(id) ?? throw new InvalidOperationException($"request {id} not in the store");
}
