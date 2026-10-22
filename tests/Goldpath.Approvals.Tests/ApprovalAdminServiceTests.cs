using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The admin views the console federates on (§7.1: the API is the contract): recent list
/// with R3 filters, the clamp, and the detail that carries trail + signatures — driven
/// over the same store seam both shipped stores implement.
/// </summary>
public class ApprovalAdminServiceTests
{
    private static readonly TimeSpan RungDeadline = TimeSpan.FromHours(8);

    private static GoldpathApprovalsOptions Options() => new GoldpathApprovalsOptions()
        .AddLadder("credit-limit", l => l
            .Rung("expert", 1_000_000m, RungDeadline)
            .TopRung("general-manager", TimeSpan.FromHours(24)))
        .AddLadder("payment-run", l => l
            .Rung("manager", 5_000_000m, RungDeadline, requiredApprovals: 2)
            .TopRung("general-manager", TimeSpan.FromHours(24)));

    private static (GoldpathApprovalEngine Engine, GoldpathApprovalsAdminService Admin, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-28T09:00:00Z"));
        var store = new GoldpathInMemoryApprovalStore();
        var options = Options();
        var engine = new GoldpathApprovalEngine(options, store, clock, NullLogger<GoldpathApprovalEngine>.Instance);
        return (engine, new GoldpathApprovalsAdminService(store, options), clock);
    }

    [Fact]
    public async Task Recent_requests_come_newest_first_with_quorum_numbers()
    {
        var (engine, admin, clock) = Build();
        var first = await engine.RequestAsync("payment-run", "P-1", 1_000_000m, "maker");
        await engine.DecideAsync(first.Id, "manager-one", "manager", true, "one of two");
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = await engine.RequestAsync("credit-limit", "K-2", 500_000m, "maker");

        var rows = await admin.GetRequestsAsync(null, null, 50, CancellationToken.None);
        Assert.Equal([second.Id, first.Id], rows.Select(r => r.Id));

        var quorum = rows.Single(r => r.Id == first.Id);
        Assert.Equal(1, quorum.SignatureCount);
        Assert.Equal(2, quorum.RequiredApprovals);
        Assert.Equal("Pending", quorum.Status);
    }

    [Fact]
    public async Task Filters_follow_R3_values_OR_within_filters_AND_together()
    {
        var (engine, admin, _) = Build();
        var pending = await engine.RequestAsync("credit-limit", "K-1", 500_000m, "maker");
        var rejected = await engine.RequestAsync("credit-limit", "K-2", 500_000m, "maker");
        await engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing");
        await engine.RequestAsync("payment-run", "P-1", 1_000_000m, "maker");

        var byStatus = await admin.GetRequestsAsync(["pending", "rejected"], null, 50, CancellationToken.None);
        Assert.Equal(3, byStatus.Count);   // values OR within the status filter

        var anded = await admin.GetRequestsAsync(["pending"], ["credit-limit"], 50, CancellationToken.None);
        Assert.Equal([pending.Id], anded.Select(r => r.Id));   // filters AND together
    }

    [Fact]
    public async Task Take_is_clamped_and_honored()
    {
        var (engine, admin, clock) = Build();
        for (var i = 0; i < 5; i++)
        {
            await engine.RequestAsync("credit-limit", $"K-{i}", 500_000m, "maker");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(2, (await admin.GetRequestsAsync(null, null, 2, CancellationToken.None)).Count);
        Assert.Single(await admin.GetRequestsAsync(null, null, -10, CancellationToken.None));   // clamp floor answers one row, honestly
    }

    [Fact]
    public async Task A_filter_searches_the_window_not_just_the_first_take_rows()
    {
        // The oldest request is the only rejected one and the only payment run; four newer
        // pending credit-limit requests sit on top of it. take = 1 must still find it.
        var (engine, admin, clock) = Build();
        var oldest = await engine.RequestAsync("payment-run", "P-old", 1_000_000m, "maker");
        await engine.DecideAsync(oldest.Id, "checker", "manager", false, "duplicate run");
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await engine.RequestAsync("credit-limit", $"K-{i}", 500_000m, "maker");
        }

        Assert.Equal([oldest.Id], (await admin.GetRequestsAsync(["rejected"], null, 1, CancellationToken.None)).Select(r => r.Id));
        Assert.Equal([oldest.Id], (await admin.GetRequestsAsync(null, ["payment-run"], 1, CancellationToken.None)).Select(r => r.Id));
    }

    [Fact]
    public void The_rung_above_an_undeclared_role_is_none()
    {
        // Three rungs, so "not found" cannot pass for "found at the bottom" by accident.
        var ladder = new GoldpathApprovalsOptions()
            .AddLadder("treasury", l => l
                .Rung("analyst", 10_000m, RungDeadline)
                .Rung("manager", 1_000_000m, RungDeadline)
                .TopRung("board", TimeSpan.FromHours(24)))
            .Ladders["treasury"];

        Assert.Null(ladder.Above(new GoldpathApprovalRung("retired-role", 1m, RungDeadline)));
        Assert.Equal("manager", ladder.Above(ladder.Rungs[0])!.Role);
        Assert.Equal("board", ladder.Above(ladder.Rungs[1])!.Role);
        Assert.Null(ladder.Above(ladder.Rungs[^1]));
    }

    [Fact]
    public async Task An_overdue_top_rung_expires_and_the_trail_says_where_it_stopped()
    {
        var (engine, admin, clock) = Build();
        var request = await engine.RequestAsync("credit-limit", "K-big", 5_000_000m, "maker");   // above the expert ceiling: the top rung
        clock.Advance(TimeSpan.FromHours(24));
        await engine.EscalateOverdueAsync();

        var detail = await admin.GetRequestAsync(request.Id, CancellationToken.None);
        var last = detail!.Trail[^1];
        Assert.Equal(("system", "expired", "overdue at top rung general-manager"), (last.Actor, last.Action, last.Detail));
    }

    [Fact]
    public async Task A_delegation_of_exactly_the_maximum_window_is_allowed()
    {
        var (engine, _, _) = Build();

        await engine.DelegateAsync("expert-one", "stand-in", new GoldpathApprovalsOptions().MaxDelegationWindow);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.DelegateAsync("expert-two", "stand-in", new GoldpathApprovalsOptions().MaxDelegationWindow + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public async Task Quorum_counts_only_the_PENDING_rung_signatures()
    {
        // A signature collected at a lower rung stays on the chain (distinct-eyes reads
        // it) but must NOT count toward the higher rung's quorum number.
        var (engine, admin, clock) = Build();
        var request = await engine.RequestAsync("payment-run", "P-esc", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "one of two");
        clock.Advance(RungDeadline);
        await engine.EscalateOverdueAsync();

        var row = (await admin.GetRequestsAsync(["Pending"], null, 50, CancellationToken.None)).Single();
        Assert.Equal("general-manager", row.PendingRole);
        Assert.Equal(0, row.SignatureCount);
        Assert.Equal(1, row.RequiredApprovals);
    }

    [Fact]
    public async Task An_orphaned_ladder_or_role_reads_a_quorum_of_one()
    {
        var (_, admin, _) = Build();
        var store = new GoldpathInMemoryApprovalStore();
        var orphanAdmin = new GoldpathApprovalsAdminService(store, Options());
        await store.AddAsync(new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = "retired-ladder",
            Subject = "K-x",
            Amount = 1m,
            RequestedBy = "maker",
            PendingRole = "ghost",
            Status = GoldpathApprovalStatus.Pending,
        });
        await store.AddAsync(new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = "payment-run",
            Subject = "K-y",
            Amount = 1m,
            RequestedBy = "maker",
            PendingRole = "retired-role",
            Status = GoldpathApprovalStatus.Pending,
        });

        var rows = await orphanAdmin.GetRequestsAsync(null, null, 50, CancellationToken.None);
        Assert.All(rows, row => Assert.Equal(1, row.RequiredApprovals));
        _ = admin;
    }

    [Fact]
    public async Task The_detail_carries_the_trail_and_the_signatures()
    {
        var (engine, admin, _) = Build();
        var request = await engine.RequestAsync("payment-run", "P-1", 1_000_000m, "maker");
        await engine.DecideAsync(request.Id, "manager-one", "manager", true, "first signature");

        var detail = await admin.GetRequestAsync(request.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(["requested", "signed"], detail!.Trail.Select(t => t.Action));
        Assert.Equal("manager-one", Assert.Single(detail.Signatures).SignedBy);

        Assert.Null(await admin.GetRequestAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
