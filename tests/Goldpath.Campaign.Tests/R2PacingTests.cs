using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>Revision R2, the pacing half: the type ceiling, priority fair share, the pause guard and orphaned releases.</summary>
public class R2PacingTests
{
    private static Action<GoldpathCampaignOptions> FastTicks(Action<GoldpathCampaignOptions>? extra = null)
        => o =>
        {
            o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
            o.LeaderTick = TimeSpan.FromMilliseconds(20);
            o.EnumerationBatchSize = 500;
            extra?.Invoke(o);
        };

    private static GoldpathCampaignPolicy Generous(GoldpathCampaignPriority priority = GoldpathCampaignPriority.Normal)
        => new(10_000, null, 100_000, null, null, "UTC") { Priority = priority };

    // ---- R2.5 the fair-share math (pure) ----

    [Fact]
    public void WeightsAre3To2To1()
    {
        Assert.Equal(3, GoldpathCampaignFairShare.WeightOf(GoldpathCampaignPriority.High));
        Assert.Equal(2, GoldpathCampaignFairShare.WeightOf(GoldpathCampaignPriority.Normal));
        Assert.Equal(1, GoldpathCampaignFairShare.WeightOf(GoldpathCampaignPriority.Low));
    }

    [Fact]
    public void SmallDemandsAreSettledFirst_AndTheRestSplitsByWeight()
    {
        // The Low claimant wants 3 and its share is 10 — it is settled in full; the 67 left
        // split 4:2 between the hungry ones, the rounding unit going to the heavier.
        var grants = GoldpathCampaignFairShare.Allocate(
            [new(4, 100), new(2, 100), new(1, 3)], 70);
        Assert.Equal([45, 22, 3], grants);
    }

    [Fact]
    public void NobodyGetsMoreThanItAskedFor_AndTheTotalIsNeverExceeded()
    {
        var grants = GoldpathCampaignFairShare.Allocate([new(4, 5), new(2, 5), new(1, 5)], 1_000);
        Assert.Equal([5, 5, 5], grants);
        Assert.Equal([0, 0], GoldpathCampaignFairShare.Allocate([new(4, 10), new(1, 10)], 0));
        Assert.Equal(7, GoldpathCampaignFairShare.Allocate([new(4, 100), new(1, 100)], 7).Sum());
    }

    [Fact]
    public void ALowClaimantIsNeverStarved()
    {
        Assert.Equal([4, 1], GoldpathCampaignFairShare.Allocate([new(4, 100), new(1, 100)], 5));
        Assert.Equal([1, 4], GoldpathCampaignFairShare.Allocate([new(1, 100), new(4, 100)], 5));
    }

    [Fact]
    public void AOneUnitTickRotatesAmongEquals()
    {
        // Three equal campaigns, one unit per tick: without the rotation the first-created
        // campaign would take every unit.
        Assert.Equal([1, 0, 0], GoldpathCampaignFairShare.Allocate([new(2, 9), new(2, 9), new(2, 9)], 1, tieBreakStart: 0));
        Assert.Equal([0, 1, 0], GoldpathCampaignFairShare.Allocate([new(2, 9), new(2, 9), new(2, 9)], 1, tieBreakStart: 1));
        Assert.Equal([0, 0, 1], GoldpathCampaignFairShare.Allocate([new(2, 9), new(2, 9), new(2, 9)], 1, tieBreakStart: 2));
        // Weight still wins over the rotation.
        Assert.Equal([0, 1, 0], GoldpathCampaignFairShare.Allocate([new(2, 9), new(4, 9), new(2, 9)], 1, tieBreakStart: 2));
    }

    [Fact]
    public void AZeroWeightIsRefused()
        => Assert.Throws<ArgumentOutOfRangeException>(() => GoldpathCampaignFairShare.Allocate([new(0, 1)], 1));

    // ---- R2.4 the type ceiling, R2.5 priority on the pacer ----

    [Fact]
    public void MaxTpsMustBePositive()
    {
        var options = new GoldpathCampaignOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            options.AddCampaign<TestTarget>("bulk", c => c.MaxTargets(10).MaxTps(0).Targets((_, _) => Targets(1))));
    }

    [Fact]
    public async Task TwoCampaignsOfOneType_ShareTheTypeCeiling_EvenlyWhenEqual()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.AddCampaign<TestTarget>("bulk", c => c
            .MaxTargets(10_000).MaxTps(500).Targets((_, _) => Targets(2_000)))));
        var (a, b) = await CreateTwoAsync(fixture, GoldpathCampaignPriority.Normal, GoldpathCampaignPriority.Normal);
        await fixture.RunPacerSliceAsync();

        var releasedA = fixture.Reload(a).ReleasedThrough;
        var releasedB = fixture.Reload(b).ReleasedThrough;
        var total = releasedA + releasedB;
        // The bucket banks at most one second of the ceiling, and the slice is under half a second.
        Assert.InRange(total, 20, 500);
        // Equal weights: neither side took more than 60% of what went out.
        Assert.InRange(releasedA, total * 0.4, total * 0.6);
    }

    [Fact]
    public async Task UnderTheTypeCeiling_HighOutpacesLow_ThreeToOne()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.AddCampaign<TestTarget>("bulk", c => c
            .MaxTargets(10_000).MaxTps(500).Targets((_, _) => Targets(2_000)))));
        var (low, high) = await CreateTwoAsync(fixture, GoldpathCampaignPriority.Low, GoldpathCampaignPriority.High);
        await fixture.RunPacerSliceAsync();

        var releasedLow = fixture.Reload(low).ReleasedThrough;
        var releasedHigh = fixture.Reload(high).ReleasedThrough;
        Assert.True(releasedLow > 0, "the Low campaign must still move");
        Assert.True(releasedHigh >= releasedLow * 2, $"High released {releasedHigh}, Low {releasedLow} — expected about 3:1");
        Assert.InRange(releasedLow + releasedHigh, 20, 500);
    }

    [Fact]
    public async Task PriorityAloneNeverThrottles_WithoutASharedCeiling()
    {
        // No MaxTps, no GlobalTps: each campaign runs at its own Tps whatever its priority.
        using var fixture = new CampaignFixture(FastTicks(o => o.AddCampaign<TestTarget>("bulk", c => c
            .MaxTargets(10_000).Targets((_, _) => Targets(50)))));
        var (low, high) = await CreateTwoAsync(fixture, GoldpathCampaignPriority.Low, GoldpathCampaignPriority.High);
        await fixture.RunPacerSliceAsync();
        Assert.Equal(50, fixture.Reload(low).ReleasedThrough);
        Assert.Equal(50, fixture.Reload(high).ReleasedThrough);
    }

    [Fact]
    public async Task PriorityIsLiveThroughTheThrottleVerb_AndShowsInTheInfo()
    {
        using var fixture = new CampaignFixture(FastTicks());
        var campaign = await fixture.CreateAsync(Generous());
        var admin = fixture.Admin();
        Assert.Equal(GoldpathCampaignPriority.Normal, (await admin.GetAsync(campaign.Id, CancellationToken.None))!.Priority);

        var result = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Priority: GoldpathCampaignPriority.High), "op", CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(GoldpathCampaignPriority.High, fixture.Reload(campaign.Id).Priority);
        Assert.Equal(GoldpathCampaignPriority.High, (await admin.GetAsync(campaign.Id, CancellationToken.None))!.Priority);
        Assert.Contains("priority=Normal -> ", fixture.Reload(campaign.Id).LastVerb, StringComparison.Ordinal);
        Assert.EndsWith("priority=High", fixture.Reload(campaign.Id).LastVerb, StringComparison.Ordinal);
    }

    // ---- R2.6 the pause guard and orphaned releases ----

    [Fact]
    public async Task AMessageThatArrivesAfterThePause_ClaimsNothing_AndResumeBringsItBack()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 3);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);
        Assert.Equal(3, fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count());

        // The operator pauses while the three messages sit in the queue.
        var admin = fixture.Admin();
        Assert.True((await admin.PauseAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        await fixture.ConsumeAllPublishedAsync();
        Assert.Empty(fixture.Executed);   // pause latency: one message, not one queue
        Assert.All(fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().ToList()),
            item => Assert.Equal(GoldpathCampaignItemState.Released, item.State));

        // Resume marks them due at once; the orphan sweep publishes them again under the allowance.
        Assert.True((await admin.ResumeAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        Assert.All(fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().ToList()),
            item => Assert.Null(item.ReleasedAt));
        var running = fixture.Reload(campaign.Id);
        Assert.Equal(2, await fixture.Engine.ReleaseOrphansAsync(scope.ServiceProvider, running, 2, CancellationToken.None));   // the allowance caps it
        Assert.Equal(1, await fixture.Engine.ReleaseOrphansAsync(scope.ServiceProvider, running, 10, CancellationToken.None));
        Assert.Equal(0, await fixture.Engine.ReleaseOrphansAsync(scope.ServiceProvider, running, 10, CancellationToken.None));   // stamped — not due again
        Assert.Equal(6, fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count());

        await fixture.ConsumeAllPublishedAsync();   // the three dropped ones are duplicates now; the three new ones execute
        Assert.Equal(3, fixture.Executed.Count);
        Assert.Equal(3, fixture.Executed.Select(e => e.Target.Id).Distinct().Count());
        Assert.Equal(3, fixture.Reload(campaign.Id).SucceededCount);
    }

    [Fact]
    public async Task AFreshReleaseIsNotAnOrphan_UntilOrphanReleaseAfterPasses()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.OrphanReleaseAfter = TimeSpan.FromMinutes(5)), sourceSize: 2);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);
        Assert.All(fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().ToList()),
            item => Assert.NotNull(item.ReleasedAt));

        clock.Now += TimeSpan.FromMinutes(4);
        Assert.Equal(0, await engine.ReleaseOrphansAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(2, await engine.ReleaseOrphansAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        Assert.Equal(4, fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count());
    }

    [Fact]
    public async Task ThePacerPublishesOrphansAgain_UnderTheSameAllowance()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.OrphanReleaseAfter = TimeSpan.Zero), sourceSize: 4);
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(10_000, null, 100, null, null, "UTC"));
        await fixture.RunPacerSliceAsync();
        Assert.Equal(4, fixture.Reload(campaign.Id).ReleasedThrough);
        // Nobody claimed them (the fleet is down); with a zero orphan age every later tick
        // publishes them again — and again — while the campaign stays Running.
        var first = fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count();
        Assert.True(first >= 4, $"expected at least the four first releases, saw {first}");
        await fixture.RunPacerSliceAsync();
        Assert.True(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count() > first);
        Assert.Equal(4, fixture.Reload(campaign.Id).ReleasedThrough);   // orphans are NOT new releases

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(4, fixture.Executed.Count);   // duplicates dropped by the claim guard
        await fixture.RunPacerSliceAsync();
        Assert.Equal(GoldpathCampaignState.Completed, fixture.Reload(campaign.Id).State);
    }

    [Fact]
    public async Task AnAbortedCampaignRefusesTheClaimToo()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 1);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        Assert.True((await fixture.Admin().AbortAsync(campaign.Id, "wrong list", "op", CancellationToken.None)).Ok);

        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.Null(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        Assert.Empty(fixture.Executed);
    }

    [Fact]
    public async Task ARetryMessageThatOutrunsTheMark_StillClaims_ButAnUnripeOneDoesNot()
    {
        // Publish-before-mark on a real broker: the retry's message can reach a consumer
        // while the row still says AwaitingRetry. Ripe = claim; unripe = a stale duplicate.
        using var fixture = new CampaignFixture(FastTicks(o => o.RetryJitter = 0), sourceSize: 1);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(10_000, null, 100, null, null, "UTC") { MaxAttempts = 3 });
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id, [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, "flaky")], CancellationToken.None);
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).State);

        Assert.Null(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));   // unripe: the ladder holds
        clock.Now += TimeSpan.FromSeconds(31);
        var claimed = await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None);   // ripe: the mark may simply be late
        Assert.NotNull(claimed);
        Assert.Equal(GoldpathCampaignItemState.Processing, claimed.State);
        Assert.Equal(1, claimed.Attempts);
    }

    // ---- helpers ----

    private static async Task<(Guid First, Guid Second)> CreateTwoAsync(
        CampaignFixture fixture, GoldpathCampaignPriority first, GoldpathCampaignPriority second)
    {
        using var scope = fixture.Services.CreateScope();
        var a = await fixture.Engine.CreateAsync(scope.ServiceProvider, "bulk", "first",
            new Dictionary<string, string>(), Generous(first), tenant: null, actor: "tester", CancellationToken.None);
        var b = await fixture.Engine.CreateAsync(scope.ServiceProvider, "bulk", "second",
            new Dictionary<string, string>(), Generous(second), tenant: null, actor: "tester", CancellationToken.None);
        return (a.Id, b.Id);
    }

    private static async IAsyncEnumerable<TestTarget> Targets(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new TestTarget(i, $"bulk{i}@example.test");
        }

        await Task.CompletedTask;
    }
}
