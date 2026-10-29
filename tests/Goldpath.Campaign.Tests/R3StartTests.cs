using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Revision R3.1 (campaign RFC, #289): a scheduled start and the Ready state. The snapshot is
/// taken when the leader adopts the campaign; nothing releases before <c>StartAt</c>; the start
/// moves until the campaign has started. Every clock here is a <see cref="TickClock"/>: a
/// leadership slice takes an exact number of ticks, so "nothing at 01:29:59Z" is an instant,
/// not a race.
/// </summary>
public class R3StartTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static GoldpathCampaignPolicy Generous(string timeZoneId = "UTC") => new(10_000, null, 100, null, null, timeZoneId);

    /// <summary>One fixture, and the engine, pacer and admin service all reading <paramref name="clock"/>.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(TickClock clock, int sourceSize = 10, Action<GoldpathCampaignOptions>? configure = null)
        {
            Clock = clock;
            Fixture = new CampaignFixture(o =>
            {
                o.LeaderTick = Tick;
                o.LeadershipSlice = TimeSpan.FromSeconds(1);   // four ticks
                configure?.Invoke(o);
            }, sourceSize);
            Engine = new GoldpathCampaignEngine<CampaignTestContext>(
                Fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
            Admin = new GoldpathCampaignAdminService<CampaignTestContext>(
                Fixture.Options, Engine, Fixture.Services.GetRequiredService<IServiceScopeFactory>(), clock,
                NullLogger<GoldpathCampaignAdminService<CampaignTestContext>>.Instance);
        }

        public TickClock Clock { get; }

        public CampaignFixture Fixture { get; }

        public GoldpathCampaignEngine<CampaignTestContext> Engine { get; }

        public GoldpathCampaignAdminService<CampaignTestContext> Admin { get; }

        public int Released => Fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count();

        /// <summary>Moves the clock to <paramref name="at"/> (never backwards).</summary>
        public void MoveTo(DateTimeOffset at)
        {
            var by = at - Clock.GetUtcNow();
            Assert.True(by >= TimeSpan.Zero, "the clock only moves forward");
            Clock.Advance(by);
        }

        /// <summary>One leadership slice (four ticks of 250 ms) through the real pacer job.</summary>
        public async Task SliceAsync()
        {
            using var scope = Fixture.Services.CreateScope();
            var job = new GoldpathCampaignPacerJob<CampaignTestContext>(
                Engine, Fixture.Options, Clock, NullLogger<GoldpathCampaignPacerJob<CampaignTestContext>>.Instance);
            var context = CampaignFixture.CreateContext(scope.ServiceProvider);
            var plan = await job.PlanAsync(context, CancellationToken.None);
            await job.ExecuteChunkAsync(CampaignFixture.MakeChunk(0, plan.ChunkPayloads[0]), context, CancellationToken.None);
        }

        public async Task<Guid> CreateAsync(GoldpathCampaignThrottle policy)
        {
            var created = await Admin.CreateAsync("winback", "scheduled push", new Dictionary<string, string>(),
                policy, tenant: null, actor: "op", CancellationToken.None);
            Assert.True(created.Ok, created.Message);
            return Fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);
        }

        public GoldpathCampaign Reload(Guid id) => Fixture.Reload(id);

        public List<GoldpathCampaignAudit> Audit(Guid id)
            => Fixture.Query(db => db.Set<GoldpathCampaignAudit>().AsNoTracking().Where(a => a.CampaignId == id).OrderBy(a => a.Id).ToList());

        public DateTimeOffset? EarliestRelease(Guid id)
            => Fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                .Where(i => i.CampaignId == id && i.ReleasedAt != null).Select(i => i.ReleasedAt).ToList())
                .Min();

        public void Dispose() => Fixture.Dispose();
    }

    // ---- the state machine ----

    [Fact]
    public async Task AFutureStart_SnapshotsToReady_AndReleasesNothingUntilTheStartPasses()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var start = t0.AddSeconds(10);
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = start });

        await rig.SliceAsync();   // 09:00:00 – 09:00:01: the snapshot completes, the start is ahead
        var ready = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Ready, ready.State);
        Assert.True(ready.EnumerationComplete);
        Assert.Equal(10, ready.EnumeratedThrough);
        Assert.Equal(0, rig.Released);

        rig.MoveTo(start.AddSeconds(-1));
        await rig.SliceAsync();   // 09:00:09 – 09:00:10: still before the start
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);
        Assert.Equal(0, rig.Released);
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "start");

        await rig.SliceAsync();   // 09:00:10 on: the first tick starts it and releases in the same tick
        var running = rig.Reload(id);
        Assert.Equal(10, rig.Released);
        Assert.Equal(start, rig.EarliestRelease(id));
        Assert.NotEqual(GoldpathCampaignState.Ready, running.State);
        Assert.Contains("started at its scheduled start 2026-11-02T09:00:10Z", running.LastVerb, StringComparison.Ordinal);
        var audit = Assert.Single(rig.Audit(id), a => a.Action == "start");
        Assert.Equal("system", audit.Actor);
        Assert.Equal(start, audit.At);
    }

    [Fact]
    public async Task NoStart_OrAStartThatPassed_RunsAsInR2()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000));

        await rig.SliceAsync();
        Assert.Equal(10, rig.Released);
        Assert.Null(rig.Reload(id).StartAt);
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "start");   // Enumerating → Running is R2's flip, not a start
    }

    [Fact]
    public async Task SpringForward_AStartAt0330BerlinReleasesNothingAt012959Z_AndReleasesAt0130Z()
    {
        // 2026-03-29 is Berlin's spring-forward Sunday: 02:00 local does not exist, and 03:30
        // CEST is 01:30Z. The start is an instant, so the gap is the console's problem, not ours.
        var start = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        Assert.Equal(new TimeOnly(3, 30), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, berlin).DateTime));

        using var rig = new Rig(new TickClock(start.AddMinutes(-40)));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000, TimeZoneId: "Europe/Berlin") { StartAt = start });
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);

        rig.MoveTo(start.AddSeconds(-1));
        await rig.SliceAsync();   // ticks at 01:29:59.00, .25, .50, .75
        Assert.Equal(0, rig.Released);
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);

        await rig.SliceAsync();   // first tick at 01:30:00Z
        Assert.Equal(10, rig.Released);
        Assert.Equal(start, rig.EarliestRelease(id));
    }

    [Fact]
    public async Task FallBack_TheRepeatedHourIsTwoInstants_AndOnlyTheOneChosenStarts()
    {
        // 2026-10-25 is Berlin's fall-back Sunday: 02:30 local happens twice, at 00:30Z (CEST)
        // and at 01:30Z (CET). The start names the second; the first must release nothing.
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var first = new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        Assert.Equal(TimeZoneInfo.ConvertTime(first, berlin).TimeOfDay, TimeZoneInfo.ConvertTime(second, berlin).TimeOfDay);

        using var rig = new Rig(new TickClock(first.AddMinutes(-5)));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000, TimeZoneId: "Europe/Berlin") { StartAt = second });
        await rig.SliceAsync();

        rig.MoveTo(first);
        await rig.SliceAsync();   // the FIRST 02:30 local
        Assert.Equal(0, rig.Released);
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);

        rig.MoveTo(second.AddSeconds(-1));
        await rig.SliceAsync();
        Assert.Equal(0, rig.Released);

        await rig.SliceAsync();   // the SECOND 02:30 local
        Assert.Equal(10, rig.Released);
        Assert.Equal(second, rig.EarliestRelease(id));
    }

    [Fact]
    public async Task AReadyCampaignPastItsEndDate_ExpiresRatherThanStarts()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000, EndDate: new DateOnly(2026, 11, 2))
        {
            StartAt = t0.AddHours(2),
        });
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);

        // The leader was gone past the end date: the start passed too, and expiry wins.
        rig.MoveTo(new DateTimeOffset(2026, 11, 3, 0, 0, 1, TimeSpan.Zero));
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.ExpiredIncomplete, rig.Reload(id).State);
        Assert.Equal(0, rig.Released);
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "start");
    }

    [Fact]
    public async Task AStartThatPassesDuringTheSnapshot_StartsWhenTheSnapshotCompletes()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var start = t0.AddSeconds(30);
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = start });

        rig.MoveTo(start.AddSeconds(5));   // the leader adopts it only now
        await rig.SliceAsync();
        Assert.Equal(10, rig.Released);
        Assert.True(rig.EarliestRelease(id) >= start);
    }

    [Fact]
    public async Task ACreateWithoutAPolicy_TakesTheTypesDefaults_WhichCarryNoStart()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var created = await rig.Admin.CreateAsync("winback", "defaults", new Dictionary<string, string>(), policy: null, null, "op", CancellationToken.None);
        Assert.True(created.Ok, created.Message);
        var row = rig.Fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single());
        Assert.Null(row.StartAt);
        Assert.Equal(rig.Fixture.Options.Type("winback").DefaultPolicy.Tps, row.Tps);

        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Running, rig.Reload(row.Id).State);
    }

    // ---- the verbs ----

    [Fact]
    public async Task MovingTheStartOfAReadyCampaign_IsAuditedOldToNew_AndTheNewStartHolds()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = t0.AddSeconds(10) });
        await rig.SliceAsync();

        var moved = await rig.Admin.ThrottleAsync(id,
            new GoldpathCampaignThrottle { StartAt = new DateTimeOffset(2026, 11, 2, 12, 0, 20, TimeSpan.FromHours(3)) }, "op", CancellationToken.None);
        Assert.True(moved.Ok, moved.Message);

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Ready, row.State);
        Assert.Equal(t0.AddSeconds(20), row.StartAt);
        Assert.Equal(TimeSpan.Zero, row.StartAt!.Value.Offset);   // stored in UTC (Npgsql refuses an offset on timestamptz)
        var audit = Assert.Single(rig.Audit(id), a => a.Action == "throttle");
        Assert.Equal("op", audit.Actor);
        Assert.EndsWith("start=2026-11-02T09:00:10Z -> tps=10000 quota=none maxInFlight=1000 window=always excludedDays=none endDate=open maxAttempts=1 priority=Normal start=2026-11-02T09:00:20Z",
            audit.Detail, StringComparison.Ordinal);

        rig.MoveTo(t0.AddSeconds(19));
        await rig.SliceAsync();   // the OLD start passed: nothing
        Assert.Equal(0, rig.Released);
        await rig.SliceAsync();   // the new one
        Assert.Equal(10, rig.Released);
        Assert.Equal(t0.AddSeconds(20), rig.EarliestRelease(id));
    }

    [Fact]
    public async Task AStartMovedAfterTheTickLoadedTheCampaign_WinsOverThePacersStart()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var (rig, scheduled) = await RaceTheStartAsync(t0, (admin, id, now) =>
            admin.ThrottleAsync(id, new GoldpathCampaignThrottle { StartAt = now.AddHours(1) }, "op", CancellationToken.None));
        using (rig)
        {
            var row = rig.Reload(scheduled);
            Assert.Equal(GoldpathCampaignState.Ready, row.State);
            Assert.Equal(t0.AddSeconds(30).AddHours(1), row.StartAt);
            Assert.Equal(0, row.ReleasedThrough);
            Assert.DoesNotContain(rig.Audit(scheduled), a => a.Action == "start");
        }
    }

    [Fact]
    public async Task APauseMadeAfterTheTickLoadedTheCampaign_WinsOverThePacersStart()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        var (rig, scheduled) = await RaceTheStartAsync(t0, (admin, id, _) => admin.PauseAsync(id, "op", CancellationToken.None));
        using (rig)
        {
            var row = rig.Reload(scheduled);
            Assert.Equal(GoldpathCampaignState.Paused, row.State);
            Assert.Equal(0, row.ReleasedThrough);
            Assert.DoesNotContain(rig.Audit(scheduled), a => a.Action == "start");
        }
    }

    /// <summary>
    /// The tick loads every workable campaign, then works them in creation order. A first campaign's
    /// selector runs inside that tick, so it can play the operator: it acts on the scheduled campaign
    /// AFTER the tick read it, when its start (t0 + 10 s) has already passed.
    /// </summary>
    private static async Task<(Rig Rig, Guid Scheduled)> RaceTheStartAsync(
        DateTimeOffset t0, Func<GoldpathCampaignAdminService<CampaignTestContext>, Guid, DateTimeOffset, Task<GoldpathAdminResult>> act)
    {
        Rig rig = null!;
        Guid scheduled = Guid.Empty;
        var acted = false;
        async IAsyncEnumerable<TestTarget> Operator()
        {
            if (scheduled != Guid.Empty && !acted)
            {
                acted = true;
                var result = await act(rig.Admin, scheduled, rig.Clock.GetUtcNow());
                Assert.True(result.Ok, result.Message);
            }

            yield return new TestTarget(1, "operator@example.test");
        }

        rig = new Rig(new TickClock(t0), configure: o => o.AddCampaign<TestTarget>("operator", c => c
            .MaxTargets(10).Targets((_, _) => Operator())));
        scheduled = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = t0.AddSeconds(10) });
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(scheduled).State);

        rig.MoveTo(t0.AddSeconds(30));   // the start has passed
        using (var scope = rig.Fixture.Services.CreateScope())
        {
            await rig.Engine.CreateAsync(scope.ServiceProvider, "operator", "the operator's hand", new Dictionary<string, string>(),
                Generous() with { StartAt = t0.AddDays(1) }, null, "tester", CancellationToken.None);
        }

        // Ticks work campaigns in creation order: the operator's must come first.
        rig.Fixture.Mutate(db => db.Set<GoldpathCampaign>().Single(c => c.Type == "operator").CreatedAt = t0.AddDays(-1));
        await rig.SliceAsync();
        Assert.True(acted);
        return (rig, scheduled);
    }

    [Fact]
    public async Task MovingTheStartOfARunningCampaign_IsRefusedInTheServersWords()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0), sourceSize: 1_000);
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10));
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Running, rig.Reload(id).State);

        var refused = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { StartAt = t0.AddHours(1) }, "op", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("has already started (Running", refused.Message, StringComparison.Ordinal);
        Assert.Contains("a scheduled start moves only before the campaign starts", refused.Message, StringComparison.Ordinal);
        var cleared = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { ClearStartAt = true }, "op", CancellationToken.None);
        Assert.False(cleared.Ok);
        Assert.Null(rig.Reload(id).StartAt);
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "throttle");

        // A paused campaign that already released has started too.
        Assert.True((await rig.Admin.PauseAsync(id, "op", CancellationToken.None)).Ok);
        Assert.True(rig.Reload(id).ReleasedThrough > 0);
        var paused = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { StartAt = t0.AddHours(1) }, "op", CancellationToken.None);
        Assert.False(paused.Ok);
        Assert.Contains("has already started (Paused", paused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartAfterTheEndDatesLocalDay_IsRefused_OnCreateAndOnThrottle()
    {
        // Istanbul is UTC+3 all year: 20:30Z on 2 November is 23:30 local (still the end day),
        // 22:00Z is 01:00 on 3 November local (after it).
        var t0 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var end = new DateOnly(2026, 11, 2);
        var after = new DateTimeOffset(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);
        var lastEvening = new DateTimeOffset(2026, 11, 2, 20, 30, 0, TimeSpan.Zero);

        var refused = await rig.Admin.CreateAsync("winback", "too late", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(TimeZoneId: "Europe/Istanbul", EndDate: end) { StartAt = after }, null, "op", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Equal("The start 2026-11-02T22:00:00Z falls on 2026-11-03 in Europe/Istanbul, after the end date 2026-11-02 — the campaign would expire before it starts. Choose an earlier start or move the end date.",
            refused.Message);
        Assert.Equal(0, rig.Fixture.Query(db => db.Set<GoldpathCampaign>().Count()));

        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(TimeZoneId: "Europe/Istanbul", EndDate: end) { StartAt = lastEvening });
        Assert.Equal(lastEvening, rig.Reload(id).StartAt);

        var moved = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { StartAt = after }, "op", CancellationToken.None);
        Assert.False(moved.Ok);
        Assert.Contains("after the end date 2026-11-02", moved.Message, StringComparison.Ordinal);

        // Pulling the end date in under a start that is still ahead is the same contradiction.
        var pulled = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle(EndDate: new DateOnly(2026, 11, 1)), "op", CancellationToken.None);
        Assert.False(pulled.Ok);
        Assert.Equal(lastEvening, rig.Reload(id).StartAt);
        Assert.Equal(end, rig.Reload(id).EndDate);
    }

    [Fact]
    public async Task AStartAtTheCalendarsEdge_IsRefusedInWords_NotAnOverflow()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var refused = await rig.Admin.CreateAsync("winback", "far", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(TimeZoneId: "Europe/Istanbul", EndDate: new DateOnly(2026, 11, 2)) { StartAt = DateTimeOffset.MaxValue },
            null, "op", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("after the end date 2026-11-02", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartInThePastMeansNow()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = t0.AddHours(1) });
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Ready, rig.Reload(id).State);

        var now = rig.Clock.GetUtcNow();
        var moved = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { StartAt = t0.AddDays(-1) }, "op", CancellationToken.None);
        Assert.True(moved.Ok, moved.Message);
        Assert.Equal(now, rig.Reload(id).StartAt);

        await rig.SliceAsync();
        Assert.Equal(10, rig.Released);
    }

    [Fact]
    public async Task ClearingTheStart_StartsAReadyCampaignOnTheNextTick()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = t0.AddDays(3) });
        await rig.SliceAsync();

        var cleared = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { ClearStartAt = true }, "op", CancellationToken.None);
        Assert.True(cleared.Ok, cleared.Message);
        Assert.Null(rig.Reload(id).StartAt);
        Assert.EndsWith("start=when-ready", Assert.Single(rig.Audit(id), a => a.Action == "throttle").Detail, StringComparison.Ordinal);

        await rig.SliceAsync();
        Assert.Equal(10, rig.Released);
        Assert.Equal("goldpath: started — its scheduled start was cleared", Assert.Single(rig.Audit(id), a => a.Action == "start").Detail);
    }

    [Fact]
    public async Task PauseAcceptsReady_AndResumeReturnsToReadyWhileTheStartIsAhead()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var start = t0.AddMinutes(10);
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000) { StartAt = start });
        await rig.SliceAsync();

        Assert.True((await rig.Admin.PauseAsync(id, "op", CancellationToken.None)).Ok);
        Assert.Equal(GoldpathCampaignState.Paused, rig.Reload(id).State);
        var resumed = await rig.Admin.ResumeAsync(id, "op", CancellationToken.None);
        Assert.True(resumed.Ok);
        Assert.Equal("resume: 'scheduled push' is now Ready.", resumed.Message);

        // Paused across its start, it resumes straight to Running.
        Assert.True((await rig.Admin.PauseAsync(id, "op", CancellationToken.None)).Ok);
        rig.MoveTo(start.AddMinutes(1));
        await rig.SliceAsync();
        Assert.Equal(0, rig.Released);   // a paused campaign does not start
        Assert.True((await rig.Admin.ResumeAsync(id, "op", CancellationToken.None)).Ok);
        Assert.Equal(GoldpathCampaignState.Running, rig.Reload(id).State);
    }

    [Fact]
    public async Task CreateAcceptsStartAt_AndTheDetailReturnsItInUtc()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var id = await rig.CreateAsync(new GoldpathCampaignThrottle(Tps: 10_000)
        {
            StartAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(3)),
        });

        var info = await rig.Admin.GetAsync(id, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), info.StartAt);
        Assert.Equal(TimeSpan.Zero, info.StartAt!.Value.Offset);
        Assert.EndsWith("start=2026-11-09T07:00:00Z)", Assert.Single(rig.Audit(id), a => a.Action == "create").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEngineStoresAnyOffsetAsUtc_ForCallersThatBypassTheAdminService()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        using var scope = rig.Fixture.Services.CreateScope();
        var campaign = await rig.Engine.CreateAsync(scope.ServiceProvider, "winback", "direct", new Dictionary<string, string>(),
            Generous() with { StartAt = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.FromHours(3)) }, null, "tester", CancellationToken.None);

        var stored = rig.Reload(campaign.Id).StartAt;
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero), stored);
        Assert.Equal(TimeSpan.Zero, stored!.Value.Offset);
    }

    [Fact]
    public void IsBeforeStart_IsStrict_AndFalseWithoutAStart()
    {
        var start = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero);
        var policy = Generous("Europe/Berlin") with { StartAt = start };
        Assert.True(policy.IsBeforeStart(start.AddTicks(-1)));
        Assert.False(policy.IsBeforeStart(start));
        Assert.False(Generous().IsBeforeStart(DateTimeOffset.MinValue));
    }

    // ---- wired: the admin HTTP surface binds the two new fields ----

    [Fact]
    public async Task TheAdminRoutes_BindStartAtAndClearStartAt_AndTheDetailCarriesStartAt()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(new TickClock(t0));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(rig.Fixture.Connection));
        builder.Services.AddSingleton(rig.Admin);
        await using var app = builder.Build();
        app.MapGoldpathCampaignAdmin<CampaignTestContext>(exposeUnsecured: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var created = await client.PostAsJsonAsync("/goldpath/admin/campaign",
            JsonDocument.Parse("""{"type":"winback","name":"over http","policy":{"tps":50,"startAt":"2026-11-09T10:00:00+03:00"}}""").RootElement);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = rig.Fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);

        var detail = JsonDocument.Parse(await client.GetStringAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}")));
        Assert.Equal("2026-11-09T07:00:00+00:00", detail.RootElement.GetProperty("startAt").GetString());

        var cleared = await client.PostAsJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}/throttle"),
            JsonDocument.Parse("""{"clearStartAt":true}""").RootElement);
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Null(rig.Reload(id).StartAt);
    }
}
