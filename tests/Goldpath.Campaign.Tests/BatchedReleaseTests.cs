using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// #285 (campaign throughput, Part 3/3): each of the pacer's three publish paths — a tick's new releases, the ripe
/// retries and the orphan re-releases — hands its messages to the seam's internal batch member as ONE batch, rather
/// than awaiting one publish per item. Publish-then-mark is unchanged: the batch completes before the durable mark,
/// and a batch that faults part way marks nothing (<c>EngineTests.CrashBetweenPublishAndMarkRepublishesButNeverDoubleClaims</c>
/// runs through the batch member, since the fixture's publisher carries it).
/// </summary>
public class BatchedReleaseTests
{
    private static Action<GoldpathCampaignOptions> FastTicks() => o =>
    {
        o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
        o.LeaderTick = TimeSpan.FromMilliseconds(20);
        o.EnumerationBatchSize = 4;
        o.RetryJitter = 0;
    };

    private static GoldpathCampaignPolicy Generous(int maxAttempts = 1) => new(10_000, null, 100_000, null, null, "UTC") { MaxAttempts = maxAttempts };

    [Fact]
    public async Task A_ticks_releases_are_one_batch_in_seq_order_and_marked_after_it()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 7);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();

        Assert.Equal(5, await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 5, CancellationToken.None));

        Assert.Equal([5], fixture.Publisher.Batches);
        Assert.Equal(0, fixture.Publisher.Singles);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Select(m => m.Seq));
        Assert.Equal(5, fixture.Reload(campaign.Id).ReleasedThrough);
    }

    [Fact]
    public async Task Nothing_to_release_publishes_no_batch()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 2);
        var campaign = await fixture.CreateAsync(Generous());
        using var scope = fixture.Services.CreateScope();

        Assert.Equal(0, await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 5, CancellationToken.None));   // nothing enumerated

        Assert.Empty(fixture.Publisher.Batches);
    }

    [Fact]
    public async Task The_ripe_retries_are_one_batch()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 3);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);
        foreach (var seq in new long[] { 1, 2, 3 })
        {
            Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, seq, CancellationToken.None));
        }

        await engine.ApplyOutcomesAsync(db, campaign.Id,
            [.. new long[] { 1, 2, 3 }.Select(seq => new GoldpathCampaignOutcomeMessage(campaign.Id, seq, false, "timeout"))], CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(31);
        fixture.Publisher.Batches.Clear();

        Assert.Equal(3, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));

        Assert.Equal([3], fixture.Publisher.Batches);
        Assert.Equal(0, fixture.Publisher.Singles);
    }

    [Fact]
    public async Task The_orphan_re_releases_are_one_batch()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 3);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);
        var admin = fixture.Admin();
        Assert.True((await admin.PauseAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        Assert.True((await admin.ResumeAsync(campaign.Id, "op", CancellationToken.None)).Ok);   // resume marks them due at once
        fixture.Publisher.Batches.Clear();

        Assert.Equal(3, await fixture.Engine.ReleaseOrphansAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 10, CancellationToken.None));

        Assert.Equal([3], fixture.Publisher.Batches);
        Assert.Equal(0, fixture.Publisher.Singles);
    }
}
