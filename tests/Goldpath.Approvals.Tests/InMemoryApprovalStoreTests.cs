using Goldpath;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The in-memory store keeps the database store's two concurrency rules (#480), so the unit suite and the database
/// agree: a write from a stale read is refused, and one identity signs a request once. It also hands out copies, as a
/// database read does, so a caller holding a request does not see — or make — changes it did not write.
/// </summary>
public sealed class InMemoryApprovalStoreTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-03T09:00:00Z");

    private static GoldpathApprovalEngine Engine(IGoldpathApprovalStore store) => new(
        new GoldpathApprovalsOptions().AddLadder("credit-limit", l => l
            .Rung("expert", 1_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
            .TopRung("general-manager", TimeSpan.FromHours(24))),
        store, new FakeTimeProvider(At), NullLogger<GoldpathApprovalEngine>.Instance);

    [Fact]
    public async Task The_store_refuses_a_second_signature_by_the_same_identity_on_one_request()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var request = Guid.NewGuid();
        await store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker", "expert", At));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker", "general-manager", At.AddMinutes(1))));
        Assert.Equal(["checker"], (await store.GetSignaturesAsync(request)).Select(s => s.SignedBy));

        await store.AddSignatureAsync(new GoldpathApprovalSignature(request, "checker-2", "expert", At.AddMinutes(2)));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(Guid.NewGuid(), "checker", "expert", At.AddMinutes(3)));
        Assert.Equal(2, (await store.GetSignaturesAsync(request)).Count);
    }

    [Fact]
    public async Task A_write_from_a_stale_read_is_refused_and_the_write_that_won_stands()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var created = await Engine(store).RequestAsync("credit-limit", "K26-480", 500_000m, "maker");
        var first = (await store.GetAsync(created.Id))!;
        var second = (await store.GetAsync(created.Id))!;

        first.Status = GoldpathApprovalStatus.Rejected;
        await store.UpdateAsync(first);
        second.Status = GoldpathApprovalStatus.Granted;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.UpdateAsync(second));

        Assert.Equal(GoldpathApprovalStatus.Rejected, (await store.GetAsync(created.Id))!.Status);
        var fresh = (await store.GetAsync(created.Id))!;
        fresh.Reason = "noted";
        await store.UpdateAsync(fresh);
        Assert.Equal("noted", (await store.GetAsync(created.Id))!.Reason);
    }

    [Fact]
    public async Task Reads_are_copies_a_change_nobody_wrote_never_reaches_the_store()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var created = await Engine(store).RequestAsync("credit-limit", "K26-481", 500_000m, "maker");

        // The engine's own returned request, a read, the worklist and the history: mutate each, write none.
        created.Status = GoldpathApprovalStatus.Granted;
        (await store.GetAsync(created.Id))!.Trail.Add(new GoldpathApprovalTrailEntry(At, "x", "forged", ""));
        (await store.GetPendingAsync())[0].PendingRole = "general-manager";
        (await store.GetRecentAsync(10))[0].DecidedBy = "x";

        var stored = (await store.GetAsync(created.Id))!;
        Assert.Equal(GoldpathApprovalStatus.Pending, stored.Status);
        Assert.Equal(["requested"], stored.Trail.Select(t => t.Action));
        Assert.Equal("expert", stored.PendingRole);
        Assert.Null(stored.DecidedBy);
    }

    [Fact]
    public async Task A_quorum_on_the_in_memory_store_still_completes_with_two_distinct_signers()
    {
        var store = new GoldpathInMemoryApprovalStore();
        var engine = Engine(store);
        var request = await engine.RequestAsync("credit-limit", "K26-482", 500_000m, "maker");

        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(request.Id, "checker-a", "expert", true, "ok"));
        Assert.Equal(GoldpathApprovalDecisionOutcome.AlreadySigned, await engine.DecideAsync(request.Id, "checker-a", "expert", true, "ok"));
        Assert.Equal(GoldpathApprovalDecisionOutcome.Applied, await engine.DecideAsync(request.Id, "checker-b", "expert", true, "ok"));
        Assert.Equal(GoldpathApprovalStatus.Granted, (await store.GetAsync(request.Id))!.Status);
        Assert.Equal(["requested", "signed", "granted"], (await store.GetAsync(request.Id))!.Trail.Select(t => t.Action));
    }
}
