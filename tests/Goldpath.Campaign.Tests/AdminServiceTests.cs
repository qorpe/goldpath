using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

public class AdminServiceTests
{
    private static readonly GoldpathCampaignPolicy Fast = new(10_000, null, 100, null, null, "UTC");

    [Fact]
    public async Task CreateRefusesAnUnknownTypeWithTeaching()
    {
        using var fixture = new CampaignFixture();
        var result = await fixture.Admin().CreateAsync("winbck", "typo", new Dictionary<string, string>(), null, null, "op", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("No campaign type named 'winbck'", result.Message, StringComparison.Ordinal);
        Assert.Contains("winback", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAppliesThePolicyPatchOverTheTypeDefault()
    {
        using var fixture = new CampaignFixture();
        var result = await fixture.Admin().CreateAsync("winback", "july",
            new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 7, DailyQuota: 42), null, "op", CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var row = fixture.Query(db => db.Set<GoldpathCampaign>().Single());
        Assert.Equal(7, row.Tps);
        Assert.Equal(42, row.DailyQuota);
        Assert.Equal(1_000, row.MaxInFlight);   // untouched default survives the patch
        Assert.Equal("op", row.CreatedBy);

        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().Single());
        Assert.Equal("create", audit.Action);
        Assert.Equal("op", audit.Actor);
        Assert.Equal(row.Id, audit.CampaignId);
        Assert.Contains("tps=7", audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TakeIsClampedAZeroAskStillAnswersOneRow()
    {
        // H8 frozen contract: take rides AdminPaging.Clamp — the floor answers one row.
        using var fixture = new CampaignFixture();
        await fixture.CreateAsync(Fast);
        await fixture.CreateAsync(Fast);

        Assert.Single(await fixture.Admin().ListAsync(null, 0, CancellationToken.None));
    }

    [Fact]
    public async Task PauseAndResumeFlipStatesWithEvidence()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        await fixture.EnumerateAllAsync(campaign);
        var admin = fixture.Admin();

        var paused = await admin.PauseAsync(campaign.Id, "op", CancellationToken.None);
        Assert.True(paused.Ok, paused.Message);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Paused, row.State);
        Assert.Equal("paused by op", row.LastVerb);

        Assert.False((await admin.PauseAsync(campaign.Id, "op", CancellationToken.None)).Ok);   // already paused

        var resumed = await admin.ResumeAsync(campaign.Id, "op2", CancellationToken.None);
        Assert.True(resumed.Ok, resumed.Message);
        Assert.Equal(GoldpathCampaignState.Running, fixture.Reload(campaign.Id).State);

        var actions = fixture.Query(db => db.Set<GoldpathCampaignAudit>().OrderBy(a => a.Id).Select(a => $"{a.Action}:{a.Actor}").ToList());
        Assert.Equal(["pause:op", "resume:op2"], actions);
    }

    [Fact]
    public async Task ResumeReturnsToEnumeratingWhenTheStreamNeverFinished()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);   // Created: enumeration not complete
        var admin = fixture.Admin();
        Assert.True((await admin.PauseAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        Assert.True((await admin.ResumeAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        Assert.Equal(GoldpathCampaignState.Enumerating, fixture.Reload(campaign.Id).State);
    }

    [Fact]
    public async Task AbortRequiresAReasonAndStampsUnsentItems()
    {
        using var fixture = new CampaignFixture(sourceSize: 4);
        var campaign = await fixture.CreateAsync(Fast);
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None);   // a consumer is mid-call
        var admin = fixture.Admin();

        Assert.False((await admin.AbortAsync(campaign.Id, " ", "op", CancellationToken.None)).Ok);

        var aborted = await admin.AbortAsync(campaign.Id, "wrong audience selected", "op", CancellationToken.None);
        Assert.True(aborted.Ok, aborted.Message);
        Assert.Contains("3 unsent items", aborted.Message, StringComparison.Ordinal);

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Aborted, row.State);
        Assert.NotNull(row.CompletedAt);
        Assert.Contains("wrong audience selected", row.LastVerb, StringComparison.Ordinal);

        var states = fixture.Query(x => x.Set<GoldpathCampaignItem>().OrderBy(i => i.Seq).Select(i => i.State).ToList());
        Assert.Equal([GoldpathCampaignItemState.Processing,   // claimed: drains gracefully, never yanked
            GoldpathCampaignItemState.Aborted, GoldpathCampaignItemState.Aborted, GoldpathCampaignItemState.Aborted], states);

        Assert.False((await admin.AbortAsync(campaign.Id, "again", "op", CancellationToken.None)).Ok);   // already terminal
        Assert.Contains("wrong audience selected", fixture.Query(x => x.Set<GoldpathCampaignAudit>()
            .Single(a => a.Action == "abort").Detail), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrottlePatchesTheLiveRowAndAuditsOldToNew()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        var result = await admin.ThrottleAsync(campaign.Id,
            new GoldpathCampaignThrottle(Tps: 50, WindowStart: new TimeOnly(9, 0), WindowEnd: new TimeOnly(18, 0), TimeZoneId: "Europe/Istanbul"),
            "op", CancellationToken.None);

        Assert.True(result.Ok, result.Message);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(50, row.Tps);
        Assert.Equal(new TimeOnly(9, 0), row.WindowStart);
        Assert.Equal("Europe/Istanbul", row.TimeZoneId);
        Assert.Equal(100, row.MaxInFlight);   // unpatched fields survive
        Assert.Contains("tps=10000", row.LastVerb, StringComparison.Ordinal);
        Assert.Contains("tps=50", row.LastVerb, StringComparison.Ordinal);

        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().Single(a => a.Action == "throttle"));
        Assert.Contains("->", audit.Detail, StringComparison.Ordinal);

        var cleared = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(ClearWindow: true), "op", CancellationToken.None);
        Assert.True(cleared.Ok, cleared.Message);
        Assert.Null(fixture.Reload(campaign.Id).WindowStart);
    }

    [Fact]
    public async Task ThrottleRefusesNonsense()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();
        Assert.False((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 0), "op", CancellationToken.None)).Ok);
        Assert.False((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(TimeZoneId: "Mars/Olympus"), "op", CancellationToken.None)).Ok);
        Assert.False((await admin.ThrottleAsync(Guid.NewGuid(), new GoldpathCampaignThrottle(Tps: 5), "op", CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ProjectionCarriesTheOperatorMath()
    {
        using var fixture = new CampaignFixture(sourceSize: 10);
        fixture.FailIds.Add(2);
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(4, null, 100, null, null, "UTC"));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 6, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var info = await fixture.Admin().GetAsync(campaign.Id, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(10, info.EnumeratedThrough);
        Assert.Equal(6, info.ReleasedThrough);
        Assert.Equal(5, info.SucceededCount);
        Assert.Equal(1, info.FailedCount);
        Assert.Equal(0, info.InFlight);
        Assert.Equal(4, info.Remaining);
        Assert.Equal(1.0, info.EtaSecondsAtCurrentTps);   // 4 remaining at 4 TPS
        Assert.True(info.WindowOpenNow);

        var failed = await fixture.Admin().GetFailedItemsAsync(campaign.Id, 10, CancellationToken.None);
        var item = Assert.Single(failed);
        Assert.Equal(2, item.Seq);
        Assert.Contains("refused target 2", item.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFiltersByStateNewestFirst()
    {
        using var fixture = new CampaignFixture();
        var admin = fixture.Admin();
        var first = await fixture.CreateAsync(Fast);
        var second = await fixture.CreateAsync(Fast);
        await admin.PauseAsync(second.Id, "op", CancellationToken.None);

        var paused = await admin.ListAsync(["paused"], 50, CancellationToken.None);
        Assert.Equal(second.Id, Assert.Single(paused).Id);
        var all = await admin.ListAsync(null, 50, CancellationToken.None);
        Assert.Equal(2, all.Count);

        var missing = await admin.GetAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(missing);
    }

    [Fact]
    public async Task CreateAuditDetailIsAnExactPolicyDescription()
    {
        using var fixture = new CampaignFixture();
        var result = await fixture.Admin().CreateAsync("winback", "july",
            new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 7, DailyQuota: 42, WindowStart: new TimeOnly(9, 0), WindowEnd: new TimeOnly(18, 0), TimeZoneId: "Europe/Istanbul"),
            null, "op", CancellationToken.None);

        Assert.True(result.Ok);
        var id = fixture.Query(db => db.Set<GoldpathCampaign>().Single()).Id;
        Assert.Equal($"Campaign 'july' created as {id:N} (winback).", result.Message);
        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().Single());
        Assert.Equal("type=winback policy=(tps=7 quota=42 maxInFlight=1000 window=09:00-18:00 Europe/Istanbul excludedDays=none endDate=open maxAttempts=1 priority=Normal)", audit.Detail);
    }

    [Fact]
    public async Task ThrottleVerbTextIsExactOldToNew()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(200, null, 1_000, null, null, "UTC"));
        var throttled = await fixture.Admin().ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), "op", CancellationToken.None);
        Assert.True(throttled.Ok);
        Assert.Equal(
            "throttled by op: tps=200 quota=none maxInFlight=1000 window=always excludedDays=none endDate=open maxAttempts=1 priority=Normal -> tps=50 quota=none maxInFlight=1000 window=always excludedDays=none endDate=open maxAttempts=1 priority=Normal",
            fixture.Reload(campaign.Id).LastVerb);
        Assert.Equal("throttle: 'test run' is now Created.", throttled.Message);
    }

    [Fact]
    public async Task ThrottleClearDailyQuotaClearsIt()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(10, 500, 100, null, null, "UTC"));
        Assert.True((await fixture.Admin().ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(ClearDailyQuota: true), "op", CancellationToken.None)).Ok);
        Assert.Null(fixture.Reload(campaign.Id).DailyQuota);
    }

    [Fact]
    public async Task ProjectionCopiesEveryRowFact()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(4, 99, 77, new TimeOnly(0, 0), new TimeOnly(23, 59), "UTC"));
        var info = (await fixture.Admin().GetAsync(campaign.Id, CancellationToken.None))!;

        Assert.Equal(campaign.Id, info.Id);
        Assert.Equal("winback", info.Type);
        Assert.Equal("test run", info.Name);
        Assert.Equal("Created", info.State);
        Assert.Equal(99, info.DailyQuota);
        Assert.Equal(0, info.ReleasedToday);
        Assert.Equal(77, info.MaxInFlight);
        Assert.Equal(new TimeOnly(0, 0), info.WindowStart);
        Assert.Equal(new TimeOnly(23, 59), info.WindowEnd);
        Assert.Equal("UTC", info.TimeZoneId);
        Assert.Equal("tester", info.CreatedBy);
        Assert.Null(info.CompletedAt);
        Assert.Null(info.LastVerb);
        Assert.Null(info.Tenant);
        Assert.False(info.EnumerationComplete);
        Assert.Null(info.EtaSecondsAtCurrentTps);   // not Running → no ETA claim
    }

    [Fact]
    public async Task EtaIsOnlyClaimedWhileRunningWithWorkLeft()
    {
        using var fixture = new CampaignFixture(sourceSize: 2);
        var campaign = await fixture.CreateAsync(Fast);
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 2, CancellationToken.None);
        var info = (await fixture.Admin().GetAsync(campaign.Id, CancellationToken.None))!;
        Assert.Equal(0, info.Remaining);
        Assert.Null(info.EtaSecondsAtCurrentTps);   // nothing left to release
        Assert.Equal(2, info.InFlight);             // released, no outcomes yet
    }

    [Fact]
    public async Task ListHonorsTakeAndIgnoresAnUnparsableStateFilter()
    {
        using var fixture = new CampaignFixture();
        await fixture.CreateAsync(Fast);
        await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();
        Assert.Single(await admin.ListAsync(null, 1, CancellationToken.None));
        Assert.Equal(2, (await admin.ListAsync(["not-a-state"], 50, CancellationToken.None)).Count);   // unknown values are ignored, as R2 did
    }

    [Fact]
    public async Task VerbsRefuseAMissingCampaignByName()
    {
        using var fixture = new CampaignFixture();
        var admin = fixture.Admin();
        var missing = Guid.NewGuid();
        var result = await admin.PauseAsync(missing, "op", CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal($"No campaign {missing:N}.", result.Message);
    }

    [Fact]
    public async Task AuditTrailReadsNewestFirst()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();
        await admin.PauseAsync(campaign.Id, "op", CancellationToken.None);
        await admin.ResumeAsync(campaign.Id, "op", CancellationToken.None);

        var trail = await admin.GetAuditAsync(campaign.Id, 10, CancellationToken.None);
        Assert.Equal(["resume", "pause"], trail.Select(a => a.Action));
    }
}
