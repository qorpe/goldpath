using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

public class EngineTests
{
    [Fact]
    public async Task CreateStampsPolicyEvidenceAndQuotaDay()
    {
        using var fixture = new CampaignFixture();
        var policy = new GoldpathCampaignPolicy(7, 42, 13, new TimeOnly(9, 0), new TimeOnly(17, 0), "Europe/Istanbul");
        var campaign = await fixture.CreateAsync(policy);

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Created, row.State);
        Assert.Equal("winback", row.Type);
        Assert.Equal(7, row.Tps);
        Assert.Equal(42, row.DailyQuota);
        Assert.Equal(13, row.MaxInFlight);
        Assert.Equal(new TimeOnly(9, 0), row.WindowStart);
        Assert.Equal(new TimeOnly(17, 0), row.WindowEnd);
        Assert.Equal("Europe/Istanbul", row.TimeZoneId);
        Assert.Equal("tester", row.CreatedBy);
        Assert.Equal(policy.LocalDay(DateTimeOffset.UtcNow), row.QuotaDay);
    }

    [Fact]
    public async Task CreateWithoutPolicyUsesTheTypeDefault()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(50, row.Tps);
        Assert.Equal(1_000, row.MaxInFlight);
        Assert.Equal("UTC", row.TimeZoneId);
    }

    [Fact]
    public async Task PolicyOfReadsTheLiveRow()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        campaign.Tps = 3;
        campaign.DailyQuota = 9;
        var policy = GoldpathCampaignEngine<CampaignTestContext>.PolicyOf(campaign);
        Assert.Equal(3, policy.Tps);
        Assert.Equal(9, policy.DailyQuota);
    }

    [Fact]
    public async Task EnumerationMaterializesInOrderAndCompletes()
    {
        using var fixture = new CampaignFixture(o => o.EnumerationBatchSize = 3, sourceSize: 8);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);

        Assert.True(campaign.EnumerationComplete);
        Assert.Equal(GoldpathCampaignState.Running, campaign.State);
        Assert.Equal(8, campaign.EnumeratedThrough);
        var items = fixture.Query(db => db.Set<GoldpathCampaignItem>().OrderBy(i => i.Seq).ToList());
        Assert.Equal(8, items.Count);
        Assert.All(items, i => Assert.Equal(GoldpathCampaignItemState.Pending, i.State));
        Assert.Contains("\"Id\":1", items[0].TargetJson, StringComparison.Ordinal);
        Assert.Contains("\"Id\":8", items[7].TargetJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatermarkResumeSkipsWithoutDuplicatingOrLosing()
    {
        using var fixture = new CampaignFixture(o => o.EnumerationBatchSize = 3, sourceSize: 8);
        var campaign = await fixture.CreateAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            var stream = await fixture.Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
            await fixture.Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
            await stream.DisposeAsync();   // the leader died mid-enumeration
        }

        Assert.Equal(3, campaign.EnumeratedThrough);
        var takenOver = fixture.Reload(campaign.Id);   // the next leader reads the row
        await fixture.EnumerateAllAsync(takenOver);

        Assert.Equal(8, takenOver.EnumeratedThrough);
        var payloads = fixture.Query(db => db.Set<GoldpathCampaignItem>().OrderBy(i => i.Seq).Select(i => i.TargetJson).ToList());
        Assert.Equal(8, payloads.Distinct().Count());
        Assert.Contains("\"Id\":4", payloads[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CeilingBreachPausesWithATeachingVerb()
    {
        using var fixture = new CampaignFixture(o => o.AddCampaign<TestTarget>("ceiling", c => c
            .MaxTargets(5)
            .Targets((_, _) => Range(1, 9))));
        using var createScope = fixture.Services.CreateScope();
        var campaign = await fixture.Engine.CreateAsync(createScope.ServiceProvider, "ceiling", "too big",
            new Dictionary<string, string>(), null, tenant: null, actor: "tester", CancellationToken.None);
        await fixture.EnumerateAllAsync(campaign);

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Paused, row.State);
        Assert.Equal(5, row.EnumeratedThrough);
        Assert.False(row.EnumerationComplete);
        Assert.Contains("target ceiling (5) exceeded", row.LastVerb, StringComparison.Ordinal);
        Assert.Contains("resume or abort", row.LastVerb, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleasePublishesCoordinatesThenMarks()
    {
        using var fixture = new CampaignFixture(sourceSize: 6);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);

        using var scope = fixture.Services.CreateScope();
        var released = await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 4, CancellationToken.None);

        Assert.Equal(4, released);
        var messages = fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().ToList();
        Assert.Equal([1L, 2L, 3L, 4L], messages.Select(m => m.Seq));
        Assert.All(messages, m => Assert.Equal(campaign.Id, m.CampaignId));
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(4, row.ReleasedThrough);
        Assert.Equal(4, row.ReleasedToday);
        var states = fixture.Query(db => db.Set<GoldpathCampaignItem>().OrderBy(i => i.Seq).Select(i => i.State).ToList());
        Assert.Equal([GoldpathCampaignItemState.Released, GoldpathCampaignItemState.Released, GoldpathCampaignItemState.Released,
            GoldpathCampaignItemState.Released, GoldpathCampaignItemState.Pending, GoldpathCampaignItemState.Pending], states);
    }

    [Fact]
    public async Task ReleaseHonorsTheBatchCeilingAndAvailability()
    {
        using var fixture = new CampaignFixture(o => o.ReleaseBatchSize = 2, sourceSize: 3);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);

        using var scope = fixture.Services.CreateScope();
        Assert.Equal(2, await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 100, CancellationToken.None));
        Assert.Equal(1, await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 100, CancellationToken.None));
        Assert.Equal(0, await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 100, CancellationToken.None));
    }

    [Fact]
    public async Task CrashBetweenPublishAndMarkRepublishesButNeverDoubleClaims()
    {
        using var fixture = new CampaignFixture(sourceSize: 3);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);

        fixture.Publisher.ThrowAfter = 2;   // dies after publishing seq 1 and 2
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None));

        // Nothing was marked: the watermark did not move, so the takeover re-publishes 1-2.
        var afterCrash = fixture.Reload(campaign.Id);
        Assert.Equal(0, afterCrash.ReleasedThrough);
        fixture.Publisher.ThrowAfter = -1;
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, afterCrash, 3, CancellationToken.None);

        var seqs = fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Select(m => m.Seq).ToList();
        Assert.Equal([1L, 2L, 1L, 2L, 3L], seqs);   // duplicates exist ON THE WIRE...

        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        Assert.Null(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));   // ...but the claim guard eats them
    }

    [Fact]
    public async Task ClaimStampsProcessingAndClaimedAt()
    {
        using var fixture = new CampaignFixture(sourceSize: 2);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        var item = await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal(GoldpathCampaignItemState.Processing, item.State);
        Assert.NotNull(item.ClaimedAt);
        Assert.Contains("\"Id\":1", item.TargetJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteItemDeserializesAndHandsContextFacts()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ExecuteItemAsync(scope.ServiceProvider, "winback", campaign.Id, 5,
            """{"Id":3,"Email":"user3@example.test"}""", "acme", attempt: 1, replay: true, CancellationToken.None);

        var (target, context) = Assert.Single(fixture.Executed);
        Assert.Equal(new TestTarget(3, "user3@example.test"), target);
        Assert.Equal(campaign.Id, context.CampaignId);
        Assert.Equal(5, context.Seq);
        Assert.Equal("winback", context.Type);
        Assert.Equal("acme", context.Tenant);
        Assert.True(context.Replay);
    }

    [Fact]
    public async Task OutcomeBatchAppliesSetBasedAndCountsRelative()
    {
        using var fixture = new CampaignFixture(sourceSize: 4);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 4, CancellationToken.None);
        for (long seq = 1; seq <= 3; seq++)
        {
            await fixture.Engine.ClaimAsync(db, campaign.Id, seq, CancellationToken.None);
        }

        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id,
        [
            new GoldpathCampaignOutcomeMessage(campaign.Id, 1, true, null),
            new GoldpathCampaignOutcomeMessage(campaign.Id, 2, true, null),
            new GoldpathCampaignOutcomeMessage(campaign.Id, 3, false, "the provider refused"),
        ], CancellationToken.None);

        var items = fixture.Query(x => x.Set<GoldpathCampaignItem>().OrderBy(i => i.Seq).ToList());
        Assert.Equal(GoldpathCampaignItemState.Succeeded, items[0].State);
        Assert.NotNull(items[0].CompletedAt);
        Assert.Equal(GoldpathCampaignItemState.Failed, items[2].State);
        Assert.Equal("the provider refused", items[2].Error);
        Assert.Equal(GoldpathCampaignItemState.Released, items[3].State);   // untouched
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(2, row.SucceededCount);
        Assert.Equal(1, row.FailedCount);
    }

    [Fact]
    public async Task QuotaDayRollsAtThePolicyMidnight()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync();
        fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id);
            row.QuotaDay = new DateOnly(2026, 1, 1);
            row.ReleasedToday = 999;
        });

        var stale = fixture.Reload(campaign.Id);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.RollQuotaDayIfNeededAsync(db, stale, CancellationToken.None);

        var rolled = fixture.Reload(campaign.Id);
        Assert.Equal(0, rolled.ReleasedToday);
        Assert.NotEqual(new DateOnly(2026, 1, 1), rolled.QuotaDay);

        // Same day again: a no-op, the counter survives.
        rolled.ReleasedToday = 5;
        await fixture.Engine.RollQuotaDayIfNeededAsync(db, rolled, CancellationToken.None);
        Assert.Equal(5, rolled.ReleasedToday);
    }

    [Fact]
    public async Task CompletionMathFlipsOnlyWhenEverythingIsTerminal()
    {
        using var fixture = new CampaignFixture(sourceSize: 2);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();

        Assert.Null(await fixture.Engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));
        Assert.Equal(GoldpathCampaignState.Running, fixture.Reload(campaign.Id).State);   // nothing released yet

        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 2, CancellationToken.None);
        Assert.Null(await fixture.Engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));
        Assert.Equal(GoldpathCampaignState.Running, fixture.Reload(campaign.Id).State);   // outcomes missing

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(GoldpathCampaignState.Completed, await fixture.Engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));
        var done = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Completed, done.State);
        Assert.NotNull(done.CompletedAt);
        Assert.Null(await fixture.Engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));   // the flip fires once
    }

    [Fact]
    public async Task FailuresCompleteAsCompletedWithFailures()
    {
        using var fixture = new CampaignFixture(sourceSize: 2);
        fixture.FailIds.Add(2);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures,
            await fixture.Engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, fixture.Reload(campaign.Id).State);
    }

    [Fact]
    public async Task ReplayHealsAFailedItemAndTheCounters()
    {
        using var fixture = new CampaignFixture(sourceSize: 2);
        fixture.FailIds.Add(2);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);

        fixture.FailIds.Remove(2);   // the operator confirmed with the provider and fixed the cause
        var itemKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{campaign.Id:N}#2");
        await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, itemKey, CancellationToken.None);

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(0, row.FailedCount);
        Assert.Equal(2, row.SucceededCount);
        var item = fixture.Query(db2 => db2.Set<GoldpathCampaignItem>().Single(i => i.Seq == 2));
        Assert.Equal(GoldpathCampaignItemState.Succeeded, item.State);
        Assert.True(fixture.Executed.Single(e => e.Target.Id == 2).Context.Replay);
    }

    [Fact]
    public async Task ReplayOfASucceededItemIsANoOp()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();
        fixture.Executed.Clear();

        var itemKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{campaign.Id:N}#1");
        await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, itemKey, CancellationToken.None);

        Assert.Empty(fixture.Executed);   // idempotent evidence, not a re-send
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);
    }

    [Fact]
    public async Task ReplayOfAnUnknownItemTeaches()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var itemKey = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{campaign.Id:N}#99");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.ReplayItemAsync(scope.ServiceProvider, itemKey, CancellationToken.None));
        Assert.Contains("No campaign item", e.Message, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<TestTarget> Range(int from, int count)
    {
        foreach (var i in Enumerable.Range(from, count))
        {
            yield return new TestTarget(i, $"user{i}@example.test");
        }

        await Task.CompletedTask;
    }
}
