using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Campaign RFC R3.5 (#298): the policy version on the throttle verb — the campaign half of
/// optimistic concurrency. The HTTP half (ETag, If-Match, 412) is admin-contract R4's.
/// </summary>
public class R3PolicyVersionTests
{
    private static readonly GoldpathCampaignPolicy Fast = new(10_000, null, 100, null, null, "UTC");

    [Fact]
    public async Task TwoThrottlesFromOneRead_TheSecondIsRefusedStale_AndTheAuditShowsOneChangeAndOneRefusal()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        // Two operators open the same screen: both read version 0.
        var read = (await admin.GetAsync(campaign.Id, CancellationToken.None))!.PolicyVersion;
        Assert.Equal(0, read);

        var first = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), read, "alice", CancellationToken.None);
        Assert.True(first.Ok, first.Message);
        Assert.False(first.Stale);
        Assert.Equal(1, first.PolicyVersion);

        var second = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(DailyQuota: 9_000), read, "bob", CancellationToken.None);
        Assert.False(second.Ok);
        Assert.True(second.Stale);
        Assert.Equal(1, second.PolicyVersion);
        Assert.Contains("version 1", second.Message, StringComparison.Ordinal);   // names the current version
        Assert.Contains("alice", second.Message, StringComparison.Ordinal);       // and who moved it
        Assert.Contains("re-read and retry", second.Message, StringComparison.Ordinal);

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(50, row.Tps);              // alice's cut stands
        Assert.Null(row.DailyQuota);            // bob's stale change never landed
        Assert.Equal(1, row.PolicyVersion);

        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().Where(a => a.CampaignId == campaign.Id).OrderBy(a => a.Id).ToList());
        Assert.Equal(["throttle", "throttle-refused"], audit.Select(a => a.Action));
        Assert.Equal("alice", audit[0].Actor);
        Assert.Equal("bob", audit[1].Actor);
        Assert.Contains("expected policy version 0", audit[1].Detail, StringComparison.Ordinal);
        Assert.Contains("current 1", audit[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrottlesWithoutAVersion_BothApply_AndTheVersionMovesByTwo()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        var first = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), expectedPolicyVersion: null, "alice", CancellationToken.None);
        var second = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 70), expectedPolicyVersion: null, "bob", CancellationToken.None);

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        Assert.Equal(1, first.PolicyVersion);
        Assert.Equal(2, second.PolicyVersion);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(70, row.Tps);              // last writer wins
        Assert.Equal(2, row.PolicyVersion);
    }

    [Fact]
    public async Task TheShippedFourArgumentThrottle_IsLastWriterWins_AndStillMovesTheVersion()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        Assert.True((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), "alice", CancellationToken.None)).Ok);
        Assert.True((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 70), "bob", CancellationToken.None)).Ok);
        Assert.Equal(2, fixture.Reload(campaign.Id).PolicyVersion);
        Assert.Equal(70, fixture.Reload(campaign.Id).Tps);
    }

    [Fact]
    public async Task ARaceLostWithoutAVersion_IsReReadAndReApplied_SoTheLastWriterStillWins()
    {
        var racer = new RaceTheThrottle();
        using var fixture = new CampaignFixture(extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racer)));
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        // bob's throttle commits between alice's read and alice's conditional update.
        racer.Arm(() => admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 7), null, "bob", CancellationToken.None));
        var alice = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), null, "alice", CancellationToken.None);

        Assert.True(racer.Fired);
        Assert.True(alice.Ok, alice.Message);
        Assert.Equal(2, alice.PolicyVersion);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(50, row.Tps);
        Assert.Equal(2, row.PolicyVersion);   // once per APPLIED throttle — the lost attempt did not count
        Assert.Equal(["bob", "alice"], fixture.Query(db => db.Set<GoldpathCampaignAudit>()
            .Where(a => a.Action == "throttle").OrderBy(a => a.Id).Select(a => a.Actor).ToList()));
    }

    [Fact]
    public async Task ARaceLostWithAVersion_IsRefusedStale_NotReApplied()
    {
        var racer = new RaceTheThrottle();
        using var fixture = new CampaignFixture(extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racer)));
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        racer.Arm(() => admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 7), null, "bob", CancellationToken.None));
        var alice = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), expectedPolicyVersion: 0, "alice", CancellationToken.None);

        Assert.True(racer.Fired);
        Assert.False(alice.Ok);
        Assert.True(alice.Stale);
        Assert.Equal(1, alice.PolicyVersion);
        Assert.Contains("bob", alice.Message, StringComparison.Ordinal);
        Assert.Equal(7, fixture.Reload(campaign.Id).Tps);
        Assert.Equal(1, fixture.Reload(campaign.Id).PolicyVersion);
    }

    [Fact]
    public async Task AStateChangeRacingAVersionedThrottle_IsNotAPolicyChange_SoTheThrottleStillApplies()
    {
        var racer = new RaceTheThrottle();
        using var fixture = new CampaignFixture(extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racer)));
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        racer.Arm(() => admin.PauseAsync(campaign.Id, "carol", CancellationToken.None));
        var alice = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), expectedPolicyVersion: 0, "alice", CancellationToken.None);

        Assert.True(racer.Fired);
        Assert.True(alice.Ok, alice.Message);
        Assert.Equal(1, alice.PolicyVersion);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(GoldpathCampaignState.Paused, row.State);   // the pause stands: the throttle never writes State
        Assert.Equal(50, row.Tps);
    }

    [Fact]
    public async Task ARaceIntoATerminalState_IsRefusedInTheVerbsWords_NotApplied()
    {
        var racer = new RaceTheThrottle();
        using var fixture = new CampaignFixture(extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racer)));
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        racer.Arm(() => admin.AbortAsync(campaign.Id, "wrong audience", "carol", CancellationToken.None));
        var alice = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), null, "alice", CancellationToken.None);

        Assert.True(racer.Fired);
        Assert.False(alice.Ok);
        Assert.False(alice.Stale);
        Assert.Contains("terminal", alice.Message, StringComparison.Ordinal);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(10_000, row.Tps);
        Assert.Equal(0, row.PolicyVersion);
    }

    [Fact]
    public async Task OnlyAnAppliedThrottleMovesTheVersion_ThePacerTheSinkPauseResumeAndAbortLeaveItAlone()
    {
        using var fixture = new CampaignFixture(o =>
        {
            o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
            o.LeaderTick = TimeSpan.FromMilliseconds(20);
        }, sourceSize: 3);
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();

        await fixture.RunPacerSliceAsync();          // the pacer enumerates and releases
        await fixture.ConsumeAllPublishedAsync();    // the sink settles outcomes
        await fixture.RunPacerSliceAsync();          // and the pacer completes it
        var settled = fixture.Reload(campaign.Id);
        Assert.Equal(3, settled.ReleasedThrough);    // the pacer and the sink really wrote this row
        Assert.Equal(3, settled.SucceededCount);
        Assert.Equal(0, settled.PolicyVersion);

        Assert.True((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 0), null, "op", CancellationToken.None)) is { Ok: false, Stale: false });
        Assert.Equal(0, fixture.Reload(campaign.Id).PolicyVersion);   // a refused throttle is not an applied one

        var second = await fixture.CreateAsync(Fast);
        Assert.True((await admin.PauseAsync(second.Id, "op", CancellationToken.None)).Ok);
        Assert.True((await admin.ResumeAsync(second.Id, "op", CancellationToken.None)).Ok);
        Assert.Equal(0, fixture.Reload(second.Id).PolicyVersion);
        var applied = await admin.ThrottleAsync(second.Id, new GoldpathCampaignThrottle(Tps: 5), null, "op", CancellationToken.None);
        Assert.Equal(1, applied.PolicyVersion);
        Assert.True((await admin.AbortAsync(second.Id, "done", "op", CancellationToken.None)).Ok);
        Assert.Equal(1, fixture.Reload(second.Id).PolicyVersion);

        var terminal = await admin.ThrottleAsync(second.Id, new GoldpathCampaignThrottle(Tps: 9), 1, "op", CancellationToken.None);
        Assert.False(terminal.Ok);
        Assert.False(terminal.Stale);
        Assert.Equal(1, terminal.PolicyVersion);   // a refusal still reports the version the row carries
    }

    [Fact]
    public async Task TheDetailAndTheListReturnThePolicyVersion()
    {
        using var fixture = new CampaignFixture();
        var campaign = await fixture.CreateAsync(Fast);
        var admin = fixture.Admin();
        Assert.True((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 50), 0, "op", CancellationToken.None)).Ok);
        Assert.True((await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 60), 1, "op", CancellationToken.None)).Ok);

        Assert.Equal(2, (await admin.GetAsync(campaign.Id, CancellationToken.None))!.PolicyVersion);
        Assert.Equal(2, (await admin.ListAsync(null, 10, CancellationToken.None)).Single().PolicyVersion);
    }

    [Fact]
    public async Task AnUnknownCampaign_AndAVersionNamedWithoutAChange_AreAnsweredPlainly()
    {
        using var fixture = new CampaignFixture();
        var admin = fixture.Admin();
        var missing = await admin.ThrottleAsync(Guid.NewGuid(), new GoldpathCampaignThrottle(Tps: 5), 0, "op", CancellationToken.None);
        Assert.False(missing.Ok);
        Assert.False(missing.Stale);
        Assert.StartsWith("No campaign", missing.Message, StringComparison.Ordinal);
        Assert.Equal("Throttle values must be positive.",   // a nonsense patch is named first, as the shipped verb did
            (await admin.ThrottleAsync(Guid.NewGuid(), new GoldpathCampaignThrottle(Tps: 0), null, "op", CancellationToken.None)).Message);

        var campaign = await fixture.CreateAsync(Fast);
        var future = await admin.ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(Tps: 5), 41, "op", CancellationToken.None);
        Assert.False(future.Ok);
        Assert.True(future.Stale);   // a version the row never had is as stale as an old one
        Assert.Equal(0, future.PolicyVersion);
        Assert.Contains("never changed", future.Message, StringComparison.Ordinal);
        Assert.Equal(10_000, fixture.Reload(campaign.Id).Tps);
    }

    /// <summary>
    /// Runs a competing verb at the exact moment the throttle's conditional UPDATE is about to
    /// execute — after it read the row, before it writes — once.
    /// </summary>
    private sealed class RaceTheThrottle : DbCommandInterceptor
    {
        private Func<Task>? _race;

        public bool Fired { get; private set; }

        public void Arm(Func<Task> race) => _race = race;

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_race is { } race && command.CommandText.Contains("\"PolicyVersion\" = ", StringComparison.Ordinal)
                && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _race = null;   // disarm first: the competing verb's own UPDATE runs through here too
                await race();
                Fired = true;
            }

            return result;
        }
    }
}
