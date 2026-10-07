using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>A clock the test turns by hand — the ladder's rungs must not cost wall time.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Revision R1 (campaign RFC): excluded days, end date, the retry ladder, the global gate.</summary>
public class R1Tests
{
    private static Action<GoldpathCampaignOptions> FastTicks(Action<GoldpathCampaignOptions>? extra = null)
        => o =>
        {
            o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
            o.LeaderTick = TimeSpan.FromMilliseconds(20);
            o.EnumerationBatchSize = 4;
            o.RetryJitter = 0;   // these tests count the R1 ladder's exact seconds; R2's jitter has its own test
            extra?.Invoke(o);
        };

    private static GoldpathCampaignPolicy Generous() => new(10_000, null, 100, null, null, "UTC");

    // ---- R1.1 excluded days ----

    [Fact]
    public async Task AnExcludedDayReleasesNothing_AndAnotherDayReleases()
    {
        using var fixture = new CampaignFixture(FastTicks());
        var today = DateTime.UtcNow.DayOfWeek;
        await fixture.CreateAsync(Generous() with { ExcludedDays = [today] });
        await fixture.RunPacerSliceAsync();
        Assert.Empty(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>());

        // The day AFTER resumes without a human: the same policy minus today's exclusion.
        var tomorrow = (DayOfWeek)(((int)today + 1) % 7);
        await fixture.Admin().ThrottleAsync(
            (await LoadOnlyAsync(fixture)).Id, new GoldpathCampaignThrottle(ExcludedDays: [tomorrow.ToString()]),
            "tester", CancellationToken.None);
        await fixture.RunPacerSliceAsync();
        Assert.NotEmpty(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>());
    }

    [Fact]
    public void ExcludedDayEvaluatesInThePolicyTimezone_AcrossDst()
    {
        // 2026-03-29 is DST-switch Sunday in Berlin: 23:30 UTC Saturday is already
        // 00:30 Sunday BERLIN time the following week-day math must respect.
        var policy = Generous() with { TimeZoneId = "Europe/Berlin", ExcludedDays = [DayOfWeek.Sunday] };
        var saturdayLateUtc = new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero);
        Assert.False(policy.IsDayAllowed(saturdayLateUtc));   // Berlin already Sunday
        Assert.Equal(new DateOnly(2026, 3, 29), policy.LocalDay(saturdayLateUtc));

        var sundayLateUtc = new DateTimeOffset(2026, 3, 29, 22, 30, 0, TimeSpan.Zero);   // Berlin 00:30 Monday (CEST)
        Assert.True(policy.IsDayAllowed(sundayLateUtc));
        Assert.Equal(new DateOnly(2026, 3, 30), policy.LocalDay(sundayLateUtc));
    }

    [Fact]
    public void ParseDaysIgnoresUnknownTokens_CaseInsensitively_AndCollapsesDuplicates()
        => Assert.Equal([DayOfWeek.Saturday, DayOfWeek.Sunday],
            GoldpathCampaignPolicy.ParseDays("saturday, SUNDAY, Fluffyday, Saturday"));

    [Fact]
    public async Task DuplicatedDayTokensCannotDodgeTheAllSevenRefusal()
    {
        using var fixture = new CampaignFixture(FastTicks());
        var campaign = await fixture.CreateAsync(Generous());
        var refused = await fixture.Admin().ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(
            ExcludedDays: ["Monday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]),
            "tester", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("pause the campaign instead", refused.Message);
    }

    // ---- R1.2 end date ----

    [Fact]
    public async Task EndDatePassingMidFlight_FlipsToExpiredIncomplete_AndTheRemainderIsTheReport()
    {
        using var fixture = new CampaignFixture(FastTicks());
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1));
        await fixture.CreateAsync(Generous() with { EndDate = yesterday });
        await fixture.RunPacerSliceAsync();

        var campaign = await LoadOnlyAsync(fixture);
        Assert.Equal(GoldpathCampaignState.ExpiredIncomplete, campaign.State);
        Assert.NotNull(campaign.CompletedAt);
        Assert.Contains("the remainder is a report", campaign.LastVerb);
        Assert.Empty(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>());

        // Terminal means terminal: throttle refuses, with the state named.
        var refused = await fixture.Admin().ThrottleAsync(
            campaign.Id, new GoldpathCampaignThrottle(Tps: 1), "tester", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("ExpiredIncomplete", refused.Message);
    }

    [Fact]
    public void TheEndDateItselfStillReleases()
    {
        var policy = Generous() with { EndDate = new DateOnly(2026, 8, 3) };
        Assert.False(policy.IsExpired(new DateTimeOffset(2026, 8, 3, 23, 0, 0, TimeSpan.Zero)));
        Assert.True(policy.IsExpired(new DateTimeOffset(2026, 8, 4, 0, 30, 0, TimeSpan.Zero)));
    }

    // ---- R1.3 the retry ladder ----

    [Fact]
    public async Task TheLadder_30sThen2m_ThenTheRepairQueue_CountedOnce()
    {
        using var fixture = new CampaignFixture(FastTicks());
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);

        var campaign = await fixture.CreateAsync(Generous() with { MaxAttempts = 3 });
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);

        // Attempt 1: the consumer CLAIMS, then fails → AWAITS, not Failed; the campaign
        // counter must not move.
        await ClaimAndFailAsync(engine, db, campaign.Id, "timeout A");
        var item = await ItemAsync(db, campaign.Id);
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(0, (await ReloadAsync(db, campaign.Id)).FailedCount);

        // The first rung is 30s: too early releases nothing, ripe releases exactly it.
        Assert.Equal(0, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(1, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));

        // Attempt 2 fails → the second rung is 2m.
        await ClaimAndFailAsync(engine, db, campaign.Id, "timeout B");
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(0, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));

        // Attempt 3 exhausts: NOW it is a durable failure — once — with the LAST error.
        await ClaimAndFailAsync(engine, db, campaign.Id, "timeout C");
        item = await ItemAsync(db, campaign.Id);
        Assert.Equal(GoldpathCampaignItemState.Failed, item.State);
        Assert.Equal(3, item.Attempts);
        Assert.Equal("timeout C", item.Error);
        Assert.Equal(1, (await ReloadAsync(db, campaign.Id)).FailedCount);
    }

    [Fact]
    public async Task ARipeItemIsNeverStarvedBehindUnripeEarlierSeqs()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 3);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous() with { MaxAttempts = 3 });
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);

        // Seq 1 and 2 fail LATE (unripe); Seq 3 failed long ago (ripe).
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 3, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id,
            [new GoldpathCampaignOutcomeMessage(campaign.Id, 3, false, "old failure")], CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 2, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id,
            [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, "fresh"),
             new GoldpathCampaignOutcomeMessage(campaign.Id, 2, false, "fresh")], CancellationToken.None);

        // Allowance 1: the ONLY ripe item is Seq 3 — it must not sit behind 1 and 2.
        Assert.Equal(1, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None));
        var third = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .SingleAsync(i => i.CampaignId == campaign.Id && i.Seq == 3);
        Assert.Equal(GoldpathCampaignItemState.Released, third.State);
    }

    [Fact]
    public async Task AnAwaitingItemBlocksCompletion_ItIsStillInFlight()
    {
        using var fixture = new CampaignFixture(FastTicks(), sourceSize: 1);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous() with { MaxAttempts = 2 });
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await ClaimAndFailAsync(engine, db, campaign.Id, "flaky");

        Assert.Null(await engine.TryCompleteAsync(db, campaign.Id, CancellationToken.None));
    }

    // ---- R1.4 the global gate ----

    [Fact]
    public async Task GlobalTpsZero_ReleasesNothing_DespiteAGenerousCampaignPolicy()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.GlobalTps = 0));
        await fixture.CreateAsync(Generous());
        await fixture.RunPacerSliceAsync();
        Assert.Empty(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>());
    }

    [Fact]
    public async Task GlobalTpsNull_IsToday_ReleasesFreely()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.GlobalTps = null));
        await fixture.CreateAsync(Generous());
        await fixture.RunPacerSliceAsync();
        Assert.NotEmpty(fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>());
    }

    // ---- helpers ----

    private static async Task<GoldpathCampaign> LoadOnlyAsync(CampaignFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CampaignTestContext>()
            .Set<GoldpathCampaign>().AsNoTracking().SingleAsync();
    }

    private static Task<GoldpathCampaign> ReloadAsync(CampaignTestContext db, Guid id)
        => db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);

    private static Task<GoldpathCampaignItem> ItemAsync(CampaignTestContext db, Guid id)
        => db.Set<GoldpathCampaignItem>().AsNoTracking().SingleAsync(i => i.CampaignId == id && i.Seq == 1);

    private static async Task ClaimAndFailAsync(
        GoldpathCampaignEngine<CampaignTestContext> engine, CampaignTestContext db, Guid id, string error)
    {
        Assert.NotNull(await engine.ClaimAsync(db, id, 1, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, id,
            [new GoldpathCampaignOutcomeMessage(id, 1, false, error)], CancellationToken.None);
    }
}
