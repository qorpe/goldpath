using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Revision R3.6 (campaign RFC, #299, slice R3-S10): auto-suspend on a failure ratio and the
/// <c>Suspended</c> state. The window's counters ride the sink's campaign update; the pacer judges
/// them once per tick from the row it loaded, suspends with a reason and a <c>system</c> audit row,
/// or restarts the window by subtracting what it read. Every clock is a <see cref="TickClock"/>, so
/// "within one tick of the flush" is an instant, not a race.
/// </summary>
public class R3SuspendTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One fixture, and the engine, pacer and admin service all reading one clock.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(int sourceSize = 10, Action<IServiceCollection>? extraServices = null)
        {
            Clock = new TickClock(T0);
            Fixture = new CampaignFixture(o =>
            {
                o.LeaderTick = Tick;
                o.LeadershipSlice = TimeSpan.FromSeconds(1);   // four ticks
            }, sourceSize, extraServices);
            Engine = new GoldpathCampaignEngine<CampaignTestContext>(
                Fixture.Options, Clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
            Admin = new GoldpathCampaignAdminService<CampaignTestContext>(
                Fixture.Options, Engine, Fixture.Services.GetRequiredService<IServiceScopeFactory>(), Clock,
                NullLogger<GoldpathCampaignAdminService<CampaignTestContext>>.Instance);
        }

        public TickClock Clock { get; }

        public CampaignFixture Fixture { get; }

        public GoldpathCampaignEngine<CampaignTestContext> Engine { get; }

        public GoldpathCampaignAdminService<CampaignTestContext> Admin { get; }

        public int Published => Fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Count();

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
            var created = await Admin.CreateAsync("winback", "fleet push", new Dictionary<string, string>(),
                policy, tenant: null, actor: "op", CancellationToken.None);
            Assert.True(created.Ok, created.Message);
            return Fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().OrderByDescending(c => c.CreatedAt).First().Id);
        }

        /// <summary>
        /// The consumer fleet and the sink: claims each listed item, then flushes ONE batch of
        /// outcomes — <paramref name="failSeqs"/> fail with <paramref name="code"/> (retryable), the rest succeed.
        /// </summary>
        public async Task ConsumeAsync(Guid id, IEnumerable<long> seqs, IReadOnlyCollection<long> failSeqs, string? code = "TIMEOUT")
        {
            var outcomes = new List<GoldpathCampaignOutcomeMessage>();
            foreach (var seq in seqs)
            {
                outcomes.Add(await ClaimAndOutcomeAsync(id, seq, failSeqs.Contains(seq), code));
            }

            await FlushAsync(id, outcomes);
        }

        public async Task<GoldpathCampaignOutcomeMessage> ClaimAndOutcomeAsync(Guid id, long seq, bool fail, string? code = "TIMEOUT")
        {
            using var scope = Fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
            var item = await Engine.ClaimAsync(db, id, seq, CancellationToken.None);
            Assert.NotNull(item);
            return fail
                ? new GoldpathCampaignOutcomeMessage(id, seq, false, "the device core timed out") { ErrorCode = code, Attempt = item.Attempts + 1 }
                : new GoldpathCampaignOutcomeMessage(id, seq, true, null) { Attempt = item.Attempts + 1 };
        }

        public async Task FlushAsync(Guid id, IReadOnlyList<GoldpathCampaignOutcomeMessage> outcomes)
        {
            using var scope = Fixture.Services.CreateScope();
            await Engine.ApplyOutcomesAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), id, outcomes, CancellationToken.None);
        }

        public GoldpathCampaign Reload(Guid id) => Fixture.Reload(id);

        public List<GoldpathCampaignAudit> Audit(Guid id)
            => Fixture.Query(db => db.Set<GoldpathCampaignAudit>().AsNoTracking().Where(a => a.CampaignId == id).OrderBy(a => a.Id).ToList());

        public void Dispose() => Fixture.Dispose();
    }

    private static GoldpathCampaignThrottle Suspending(double? ratio = 0.3, int? window = 100, int? minSample = 4, int? maxAttempts = null)
        => new(Tps: 10_000, MaxInFlight: 1_000, MaxAttempts: maxAttempts)
        {
            SuspendOnFailureRatio = ratio,
            SuspendWindow = window,
            SuspendMinSample = minSample,
        };

    private static IEnumerable<long> Seqs(long from, long to) => Enumerable.Range((int)from, (int)(to - from + 1)).Select(i => (long)i);

    // ---- the proof the issue names ----

    [Fact]
    public async Task CrossingTheRatio_SuspendsWithinOneTickOfTheFlush_WithItsReasonAndASystemAuditRow()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();   // snapshot + every item released
        Assert.Equal(10, rig.Published);

        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        var flushed = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Running, flushed.State);   // the sink counts; only the pacer judges
        Assert.Equal(10, flushed.WindowOutcomes);
        Assert.Equal(5, flushed.WindowFailures);

        var tickOfTheFlush = rig.Clock.GetUtcNow();
        await rig.SliceAsync();

        var suspended = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Suspended, suspended.State);
        Assert.Equal(
            "failure ratio 0.5 ≥ 0.3 over 10 outcomes since the campaign began (minimum sample 4); most frequent code TIMEOUT (100%)",
            suspended.SuspendedReason);
        Assert.Equal("goldpath: suspended — " + suspended.SuspendedReason, suspended.LastVerb);
        var audit = Assert.Single(rig.Audit(id), a => a.Action == "suspend");
        Assert.Equal("system", audit.Actor);
        Assert.Equal(tickOfTheFlush, audit.At);   // the first tick after the flush
        Assert.Equal(suspended.SuspendedReason, audit.Detail);

        var info = await rig.Admin.GetAsync(id, CancellationToken.None);
        Assert.Equal("Suspended", info!.State);
        Assert.Equal(suspended.SuspendedReason, info.SuspendedReason);
        Assert.Equal(0.5, info.FailureRatioNow);
        Assert.Equal(0.3, info.SuspendOnFailureRatio);
        Assert.Equal(100, info.SuspendWindow);
        Assert.Equal(4, info.SuspendMinSample);
    }

    [Fact]
    public async Task BelowTheMinimumSample_NothingSuspends_WhateverTheRatio()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(ratio: 0.1, minSample: 11));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [.. Seqs(1, 10)]);   // a ratio of 1.0 over 10 < 11

        await rig.SliceAsync();

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, row.State);
        Assert.Null(row.SuspendedReason);
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "suspend");
    }

    [Fact]
    public async Task ANullRatio_NeverSuspends()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(ratio: null, minSample: 1));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [.. Seqs(1, 10)]);

        await rig.SliceAsync();

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, row.State);
        Assert.Null(row.SuspendOnFailureRatio);
        Assert.Equal(10, row.WindowFailures);   // counted all the same — the detail shows the ratio
        Assert.DoesNotContain(rig.Audit(id), a => a.Action == "suspend");
    }

    [Fact]
    public async Task ASuspendedCampaign_ClaimsNothing_AndReleasesNothingMore()
    {
        using var rig = new Rig(sourceSize: 20);
        var id = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();
        Assert.Equal(20, rig.Published);
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5, 6]);

        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Suspended, rig.Reload(id).State);

        // Items 11–20 were released before the suspension; their messages arrive now and are refused.
        using (var scope = rig.Fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
            foreach (var seq in Seqs(11, 20))
            {
                Assert.Null(await rig.Engine.ClaimAsync(db, id, seq, CancellationToken.None));
            }
        }

        // Long past the orphan delay: a suspended campaign re-publishes nothing either.
        rig.Clock.Advance(rig.Fixture.Options.OrphanReleaseAfter + TimeSpan.FromMinutes(1));
        await rig.SliceAsync();
        Assert.Equal(20, rig.Published);
        Assert.Equal(GoldpathCampaignState.Suspended, rig.Reload(id).State);
    }

    [Fact]
    public async Task Resume_RestartsTheWindow_SoTheOldFailuresDoNotSuspendItAgain()
    {
        using var rig = new Rig(sourceSize: 20);
        var id = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Suspended, rig.Reload(id).State);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var resumedAt = rig.Clock.GetUtcNow();

        var resumed = await rig.Admin.ResumeAsync(id, "op", CancellationToken.None);

        Assert.True(resumed.Ok, resumed.Message);
        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Running, row.State);
        Assert.Null(row.SuspendedReason);
        Assert.Equal(0, row.WindowOutcomes);
        Assert.Equal(0, row.WindowFailures);
        Assert.Equal(resumedAt, row.WindowStartedAt);
        Assert.Equal("op", Assert.Single(rig.Audit(id), a => a.Action == "resume").Actor);
        // R2.6 as after a pause: the released, unclaimed items are due again at once.
        Assert.All(rig.Fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                .Where(i => i.CampaignId == id && i.State == GoldpathCampaignItemState.Released).ToList()),
            i => Assert.Null(i.ReleasedAt));

        await rig.SliceAsync();   // the old 5-in-10 is gone from the window
        Assert.Equal(GoldpathCampaignState.Running, rig.Reload(id).State);
        Assert.Single(rig.Audit(id), a => a.Action == "suspend");

        await rig.ConsumeAsync(id, Seqs(11, 20), failSeqs: [11]);   // 1 in 10 < 0.3 since the resume
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, rig.Reload(id).State);
        Assert.Single(rig.Audit(id), a => a.Action == "suspend");
    }

    [Fact]
    public async Task AFullWindowBelowTheRatio_RestartsBySubtracting_AndAFlushDuringTheRestartLosesNoOutcome()
    {
        GoldpathCampaignEngine<CampaignTestContext>? engine = null;
        Microsoft.Data.Sqlite.SqliteConnection? connection = null;
        Guid campaignId = default;
        IReadOnlyList<GoldpathCampaignOutcomeMessage> late = [];
        var racing = new FlushRacesTheRestart(async () =>
        {
            using var scope = new CampaignTestContext(new DbContextOptionsBuilder<CampaignTestContext>()
                .UseSqlite(connection!).Options);
            await engine!.ApplyOutcomesAsync(scope, campaignId, late, CancellationToken.None);
        });
        using var rig = new Rig(sourceSize: 13, extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racing)));
        connection = rig.Fixture.Connection;
        engine = rig.Engine;
        campaignId = await rig.CreateAsync(Suspending(ratio: 0.5, window: 10, minSample: 10));
        await rig.SliceAsync();
        await rig.ConsumeAsync(campaignId, Seqs(1, 10), failSeqs: [1]);   // a full window at 0.1
        // Three more outcomes, claimed now, flushed by the sink at the instant the pacer restarts the window.
        late = [
            await rig.ClaimAndOutcomeAsync(campaignId, 11, fail: true),
            await rig.ClaimAndOutcomeAsync(campaignId, 12, fail: true),
            await rig.ClaimAndOutcomeAsync(campaignId, 13, fail: true),
        ];
        var restartTick = rig.Clock.GetUtcNow();

        await rig.SliceAsync();

        Assert.True(racing.Fired);
        var row = rig.Reload(campaignId);
        Assert.Equal(3, row.WindowOutcomes);   // 10 + 3 − the 10 it read: the late three are kept
        Assert.Equal(3, row.WindowFailures);
        Assert.Equal(restartTick, row.WindowStartedAt);
        Assert.NotEqual(GoldpathCampaignState.Suspended, row.State);   // 3 outcomes < the minimum sample
        Assert.Equal(13, row.SucceededCount + row.FailedCount);
    }

    [Fact]
    public async Task TheReasonsCode_IsReadFromTheCurrentWindowOnly_NotFromTheFailuresBeforeItRestarted()
    {
        using var rig = new Rig(sourceSize: 20);
        var id = await rig.CreateAsync(Suspending(ratio: 0.5, window: 10, minSample: 10));
        await rig.SliceAsync();
        Assert.Equal(20, rig.Published);

        // A full window below the ratio, failing with A: it restarts, a second after those failures completed.
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4], code: "A");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.SliceAsync();
        var restarted = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Running, restarted.State);
        Assert.Equal(0, restarted.WindowOutcomes);

        // The next window crosses the ratio, failing with B only.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.ConsumeAsync(id, Seqs(11, 20), failSeqs: [.. Seqs(11, 20)], code: "B");
        await rig.SliceAsync();

        var suspended = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Suspended, suspended.State);
        // The four A failures still sit on the ladder (AwaitingRetry); read without the window's filter
        // the reason would say "B (71%)" — the code of an older window bleeding into this one.
        Assert.EndsWith("most frequent code B (100%)", suspended.SuspendedReason);
    }

    // ---- counting ----

    [Fact]
    public async Task RetryableFailuresCount_AcceptancesDoNot_AndADuplicateOutcomeMovesNothing()
    {
        using var rig = new Rig(sourceSize: 3);
        var id = await rig.CreateAsync(Suspending(ratio: 0.9, maxAttempts: 3));
        await rig.SliceAsync();
        var retry = await rig.ClaimAndOutcomeAsync(id, 1, fail: true);
        var success = await rig.ClaimAndOutcomeAsync(id, 2, fail: false);
        await rig.ClaimAndOutcomeAsync(id, 3, fail: false);
        var accepted = new GoldpathCampaignOutcomeMessage(id, 3, false, null)
        {
            Accepted = true,
            CorrelationId = "dev-3",
            AckDeadline = T0.AddHours(1),
            Attempt = 1,
        };

        await rig.FlushAsync(id, [retry, success, accepted]);
        await rig.FlushAsync(id, [retry, success]);   // redelivered: the guards match nothing

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry,
            rig.Fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single(i => i.Seq == 1)).State);
        Assert.Equal(2, row.WindowOutcomes);   // the retry and the success; the acceptance is not an outcome yet
        Assert.Equal(1, row.WindowFailures);
        Assert.Equal(0, row.FailedCount);       // a retry is not a terminal failure — only a window failure
    }

    [Fact]
    public async Task AnInterruptedClaim_IsAFailedOutcome_InTheWindow()
    {
        using var rig = new Rig(sourceSize: 1);
        var id = await rig.CreateAsync(Suspending(ratio: 0.9));
        await rig.SliceAsync();
        await rig.ClaimAndOutcomeAsync(id, 1, fail: false);   // claimed; the consumer dies before its outcome
        rig.Clock.Advance(rig.Fixture.Options.StaleClaimAfter + TimeSpan.FromSeconds(1));

        await rig.SliceAsync();

        var row = rig.Reload(id);
        Assert.Equal(1, row.FailedCount);
        Assert.Equal(1, row.WindowOutcomes);
        Assert.Equal(1, row.WindowFailures);
    }

    [Fact]
    public async Task TheLadderKeepsRunningOnASuspendedCampaign()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(maxAttempts: 3));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 9), failSeqs: [1, 2, 3, 4, 5]);
        var lateFailure = await rig.ClaimAndOutcomeAsync(id, 10, fail: true);
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Suspended, rig.Reload(id).State);

        await rig.FlushAsync(id, [lateFailure]);   // a call that was in flight fails after the suspension

        var item = rig.Fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single(i => i.Seq == 10));
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);   // a resume finds it waiting, as after a pause
        Assert.Equal(0, rig.Reload(id).FailedCount);
    }

    // ---- the verbs ----

    [Fact]
    public async Task Pause_RefusesASuspendedCampaign_AbortAndThrottleAcceptIt()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        await rig.SliceAsync();

        var paused = await rig.Admin.PauseAsync(id, "op", CancellationToken.None);
        Assert.False(paused.Ok);
        Assert.Contains("already stopped", paused.Message, StringComparison.Ordinal);
        Assert.Contains("Resume it, or abort it", paused.Message, StringComparison.Ordinal);
        Assert.Equal(GoldpathCampaignState.Suspended, rig.Reload(id).State);

        var throttled = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle(Tps: 5), "op", CancellationToken.None);
        Assert.True(throttled.Ok, throttled.Message);
        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Suspended, row.State);
        Assert.Equal(5, row.Tps);
        Assert.NotNull(row.SuspendedReason);   // a throttle during the suspension keeps the reason

        var aborted = await rig.Admin.AbortAsync(id, "the device core is down", "op", CancellationToken.None);
        Assert.True(aborted.Ok, aborted.Message);
        Assert.Equal(GoldpathCampaignState.Aborted, rig.Reload(id).State);
    }

    [Fact]
    public async Task TurningTheRatioOn_RestartsTheWindow_SoOutcomesFromBeforeItDoNotJudgeTheCampaign()
    {
        using var rig = new Rig(sourceSize: 20);
        var id = await rig.CreateAsync(Suspending(ratio: null));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        Assert.Equal(5, rig.Reload(id).WindowFailures);

        var on = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { SuspendOnFailureRatio = 0.3 }, "op", CancellationToken.None);

        Assert.True(on.Ok, on.Message);
        var row = rig.Reload(id);
        Assert.Equal(0.3, row.SuspendOnFailureRatio);
        Assert.Equal(0, row.WindowOutcomes);
        Assert.Equal(0, row.WindowFailures);
        Assert.Contains("suspendOn=off", row.LastVerb, StringComparison.Ordinal);
        Assert.Contains("suspendOn=0.3", row.LastVerb, StringComparison.Ordinal);
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.Running, rig.Reload(id).State);

        var off = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { ClearSuspendOnFailureRatio = true }, "op", CancellationToken.None);
        Assert.True(off.Ok, off.Message);
        Assert.Null(rig.Reload(id).SuspendOnFailureRatio);
    }

    [Fact]
    public async Task ARefusedThrottle_LeavesTheWindowAsItWas()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(ratio: null));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2]);

        // A sample larger than the window would never suspend: refused, and nothing is written.
        var refused = await rig.Admin.ThrottleAsync(id,
            new GoldpathCampaignThrottle { SuspendOnFailureRatio = 0.3, SuspendWindow = 50, SuspendMinSample = 60 }, "op", CancellationToken.None);

        Assert.False(refused.Ok);
        var row = rig.Reload(id);
        Assert.Null(row.SuspendOnFailureRatio);
        Assert.Equal(10, row.WindowOutcomes);
        Assert.Equal(2, row.WindowFailures);
        Assert.Null(row.WindowStartedAt);

        // A finished campaign is refused too, and its counters stay its record.
        await rig.SliceAsync();
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, rig.Reload(id).State);
        var terminal = await rig.Admin.ThrottleAsync(id, new GoldpathCampaignThrottle { SuspendOnFailureRatio = 0.3 }, "op", CancellationToken.None);
        Assert.False(terminal.Ok);
        Assert.Equal(10, rig.Reload(id).WindowOutcomes);
        Assert.Null(rig.Reload(id).WindowStartedAt);
    }

    [Fact]
    public async Task TwoRestartsOnOneRead_NeverDriveACounterNegative()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(ratio: null));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2]);
        var at = rig.Clock.GetUtcNow();

        using var scope = rig.Fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.Equal(1, await GoldpathCampaignEngine<CampaignTestContext>.RestartWindowAsync(db, id, 10, 2, at, CancellationToken.None));
        Assert.Equal(0, await GoldpathCampaignEngine<CampaignTestContext>.RestartWindowAsync(db, id, 10, 2, at, CancellationToken.None));

        var row = rig.Reload(id);
        Assert.Equal(0, row.WindowOutcomes);
        Assert.Equal(0, row.WindowFailures);
    }

    [Fact]
    public async Task AThrottleThatClearsTheRatioBetweenTheLoadAndTheFlip_Wins()
    {
        GoldpathCampaignAdminService<CampaignTestContext>? admin = null;
        Guid campaignId = default;
        // The pacer loaded the row with the ratio set; an operator clears it the instant before the flip.
        var racing = new BeforeCommand("\"SuspendedReason\" =", async () =>
        {
            var cleared = await admin!.ThrottleAsync(campaignId, new GoldpathCampaignThrottle { ClearSuspendOnFailureRatio = true }, "op", CancellationToken.None);
            Assert.True(cleared.Ok, cleared.Message);
        });
        using var rig = new Rig(sourceSize: 10, extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(racing)));
        admin = rig.Admin;
        campaignId = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();
        await rig.ConsumeAsync(campaignId, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);

        await rig.SliceAsync();

        Assert.True(racing.Fired);
        var row = rig.Reload(campaignId);
        Assert.NotEqual(GoldpathCampaignState.Suspended, row.State);   // the flip's guard on the ratio matched nothing
        Assert.Null(row.SuspendedReason);
        Assert.DoesNotContain(rig.Audit(campaignId), a => a.Action == "suspend");
    }

    [Theory]
    [InlineData(0.0, 100, 10, "outside (0, 1]")]
    [InlineData(-0.2, 100, 10, "outside (0, 1]")]
    [InlineData(1.01, 100, 10, "outside (0, 1]")]
    [InlineData(double.NaN, 100, 10, "outside (0, 1]")]
    [InlineData(0.3, 100, 101, "larger than the window")]
    [InlineData(0.3, 0, 10, "must be positive")]
    [InlineData(0.3, 100, -1, "must be positive")]
    public async Task AnUnusableSuspendPolicy_IsRefusedWithTeachingText_OnCreateAndOnThrottle(double ratio, int window, int minSample, string expected)
    {
        using var rig = new Rig();
        var bad = Suspending(ratio, window, minSample);

        var created = await rig.Admin.CreateAsync("winback", "x", new Dictionary<string, string>(), bad, null, "op", CancellationToken.None);
        Assert.False(created.Ok);
        Assert.Contains(expected, created.Message, StringComparison.Ordinal);

        var id = await rig.CreateAsync(Suspending(ratio: null));
        var throttled = await rig.Admin.ThrottleAsync(id, bad with { Tps = null, MaxInFlight = null }, "op", CancellationToken.None);
        Assert.False(throttled.Ok);
        Assert.True(throttled.Message.Contains(expected, StringComparison.Ordinal) || throttled.Message.Contains("must be positive", StringComparison.Ordinal),
            throttled.Message);
        Assert.Null(rig.Reload(id).SuspendOnFailureRatio);
    }

    [Fact]
    public async Task ARatioOfOne_IsAccepted_AndSuspendsOnlyWhenEveryOutcomeFailed()
    {
        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending(ratio: 1.0, minSample: 10));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [.. Seqs(1, 10)], code: null);
        await rig.SliceAsync();

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Suspended, row.State);
        Assert.EndsWith("most frequent code (none) (100%)", row.SuspendedReason, StringComparison.Ordinal);
    }

    // ---- the reason's words ----

    [Fact]
    public void TheReason_IsCultureInvariant_AndNamesTheMostFrequentCode()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var reason = GoldpathCampaignPacerJob<CampaignTestContext>.SuspendReason(
                0.3, 400, 208, new DateTimeOffset(2026, 11, 2, 10, 42, 5, TimeSpan.FromHours(3)), 100,
                [.. Enumerable.Repeat<string?>("TIMEOUT", 61), .. Enumerable.Repeat<string?>("İPTAL", 39)]);

            Assert.Equal(
                "failure ratio 0.52 ≥ 0.3 over 400 outcomes since 2026-11-02T07:42:05Z (minimum sample 100); most frequent code TIMEOUT (61%)",
                reason);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void TheReason_BreaksATieByCode_AndSaysSoWhenNoFailureWasSampled()
    {
        Assert.EndsWith("most frequent code A (50%)",
            GoldpathCampaignPacerJob<CampaignTestContext>.SuspendReason(0.5, 4, 2, null, 1, ["B", "A"]), StringComparison.Ordinal);
        Assert.EndsWith("; no failure sampled",
            GoldpathCampaignPacerJob<CampaignTestContext>.SuspendReason(0.5, 4, 2, null, 1, []), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReasonSample_StopsAt500_AndTheReasonFitsItsColumn()
    {
        var sample = new SampleQuery();
        using var rig = new Rig(sourceSize: 600, extraServices: s => s.ConfigureDbContext<CampaignTestContext>(o => o.AddInterceptors(sample)));
        var id = await rig.CreateAsync(Suspending(ratio: 0.5, window: 1_000, minSample: 600));
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 600), failSeqs: [.. Seqs(1, 600)], code: new string('X', 64));

        await rig.SliceAsync();

        var row = rig.Reload(id);
        Assert.Equal(GoldpathCampaignState.Suspended, row.State);
        Assert.Contains("over 600 outcomes", row.SuspendedReason, StringComparison.Ordinal);
        Assert.Contains("(100%)", row.SuspendedReason, StringComparison.Ordinal);
        Assert.True(row.SuspendedReason!.Length <= 512);
        Assert.True(row.LastVerb!.Length <= 512);
        var limit = Assert.Single(sample.Limits);   // the sample is read once, at the suspension
        Assert.Equal(500, limit);
    }

    // ---- the meter ----

    [Fact]
    public async Task ASuspension_IsCountedInGoldpathCampaignSuspendedTotal_ByType()
    {
        var counted = new List<(long Value, string? Type)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Goldpath.Campaign" && instrument.Name == "goldpath_campaign_suspended_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "type")
                {
                    lock (counted)
                    {
                        counted.Add((value, tag.Value as string));
                    }
                }
            }
        });
        listener.Start();

        using var rig = new Rig(sourceSize: 10);
        var id = await rig.CreateAsync(Suspending());
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        await rig.SliceAsync();
        await rig.SliceAsync();   // already suspended: not counted twice

        lock (counted)
        {
            Assert.Contains((1L, "winback"), counted);
        }

        Assert.Single(rig.Audit(id), a => a.Action == "suspend");
    }

    // ---- wired: the admin HTTP surface binds the new fields ----

    [Fact]
    public async Task TheAdminRoutes_BindTheSuspendFields_AndTheDetailCarriesTheRatioAndTheReason()
    {
        using var rig = new Rig(sourceSize: 10);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(rig.Fixture.Connection));
        builder.Services.AddSingleton(rig.Admin);
        await using var app = builder.Build();
        app.MapGoldpathCampaignAdmin<CampaignTestContext>(exposeUnsecured: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var created = await client.PostAsJsonAsync("/goldpath/admin/campaign",
            JsonDocument.Parse("""{"type":"winback","name":"over http","policy":{"tps":10000,"maxInFlight":1000,"suspendOnFailureRatio":0.3,"suspendWindow":200,"suspendMinSample":4}}""").RootElement);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = rig.Fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);
        await rig.SliceAsync();
        await rig.ConsumeAsync(id, Seqs(1, 10), failSeqs: [1, 2, 3, 4, 5]);
        await rig.SliceAsync();

        var detail = JsonDocument.Parse(await client.GetStringAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}"))).RootElement;
        Assert.Equal("Suspended", detail.GetProperty("state").GetString());
        Assert.Equal(0.3, detail.GetProperty("suspendOnFailureRatio").GetDouble());
        Assert.Equal(200, detail.GetProperty("suspendWindow").GetInt32());
        Assert.Equal(4, detail.GetProperty("suspendMinSample").GetInt32());
        Assert.Equal(0.5, detail.GetProperty("failureRatioNow").GetDouble());
        Assert.StartsWith("failure ratio 0.5 ≥ 0.3 over 10 outcomes", detail.GetProperty("suspendedReason").GetString(), StringComparison.Ordinal);

        var listed = JsonDocument.Parse(await client.GetStringAsync("/goldpath/admin/campaign?state=Suspended")).RootElement;
        Assert.Equal(1, listed.GetArrayLength());

        var cleared = await client.PostAsJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}/throttle"),
            JsonDocument.Parse("""{"clearSuspendOnFailureRatio":true}""").RootElement);
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        var resumed = await client.PostAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}/resume"), null);
        Assert.True(resumed.IsSuccessStatusCode, await resumed.Content.ReadAsStringAsync());
        var row = rig.Reload(id);
        Assert.Null(row.SuspendOnFailureRatio);
        Assert.Equal(GoldpathCampaignState.Running, row.State);
    }

    /// <summary>Runs <paramref name="before"/> once, the instant before the first UPDATE whose text holds <paramref name="marker"/>.</summary>
    private sealed class BeforeCommand(string marker, Func<Task> before) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains(marker, StringComparison.Ordinal))
            {
                Fired = true;
                await before();
            }

            return result;
        }
    }

    /// <summary>Records the row limit of every read of the items' error codes (the suspension's sample).</summary>
    private sealed class SampleQuery : DbCommandInterceptor
    {
        public List<long> Limits { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // The sample is the one read that selects the error code alone.
            var text = command.CommandText;
            var from = text.IndexOf("FROM \"GoldpathCampaignItems\"", StringComparison.Ordinal);
            if (text.StartsWith("SELECT ", StringComparison.Ordinal) && from > 0
                && text[..from].Trim().EndsWith(".\"ErrorCode\"", StringComparison.Ordinal) && !text[..from].Contains(',', StringComparison.Ordinal))
            {
                var limit = System.Text.RegularExpressions.Regex.Match(text, @"LIMIT (?:(\d+)|(@\w+))");
                Assert.True(limit.Success, text);   // an unbounded sample is the defect this test exists for
                Limits.Add(limit.Groups[1].Success
                    ? long.Parse(limit.Groups[1].Value, CultureInfo.InvariantCulture)
                    : Convert.ToInt64(command.Parameters[limit.Groups[2].Value].Value, CultureInfo.InvariantCulture));
            }

            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Runs the sink's flush at the exact moment the pacer's window-restart UPDATE is about to execute.</summary>
    private sealed class FlushRacesTheRestart(Func<Task> flush) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("\"WindowStartedAt\" =", StringComparison.Ordinal))
            {
                Fired = true;
                await flush();
            }

            return result;
        }
    }
}
