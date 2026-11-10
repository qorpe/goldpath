using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-FLEET-02 (#349; the rig spec the owner signed on #326, in <c>docs/strategy/scenario-campaign.md</c>):
/// ONE campaign over the full 30M synthetic targets, seeded before the measured hour, then released in
/// real time for 60 minutes through the whole path on real RabbitMQ + PostgreSQL — the shared
/// <see cref="CampaignReleaseRig"/>, the same host as S-FLEET-02a. It writes the per-second series (at the
/// publish instant, and at the send instant as a diagnostic) and the verdict to <c>GOLDPATH_BENCH_OUT</c>,
/// and <c>scripts/bench-campaign.sh fleet02</c> samples the headroom beside it.
/// <para>
/// It is OPT-IN: it skips unless <c>GOLDPATH_BENCH_FLEET02=1</c>, because bench.yml's monthly
/// <c>Category=Bench</c> run has a 90-minute cap and this one needs hours. Knobs (environment):
/// <c>GOLDPATH_BENCH_CAMPAIGN_SECONDS</c> (3600), <c>_TPS</c> (2000), <c>_TARGETS</c> (30,000,000 —
/// decoupled from the window: the spec says 30M, never shrunk), <c>_FLOOR_TPS</c> (2000, the signed
/// mean), <c>_CONSUMERS</c> (32, the bus-level knob; the peak the run actually reached is reported),
/// <c>_LATENCY_MS</c> (20) and <c>_SEED_MINUTES</c> (180, the most the seeding may take).
/// </para>
/// </summary>
[Trait("Category", "Bench")]
[Collection(CampaignFleet02BenchCollection.Name)]
public sealed class CampaignFleet02BenchTests : IAsyncLifetime
{
    private static readonly int Seconds = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_SECONDS", 3_600);
    private static readonly int Tps = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_TPS", 2_000);
    private static readonly long Targets = CampaignReleaseRig.Knob("GOLDPATH_BENCH_CAMPAIGN_TARGETS", 30_000_000);
    private static readonly int FloorTps = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_FLOOR_TPS", 2_000);
    private static readonly int Consumers = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_CONSUMERS", 32);
    private static readonly int LatencyMs = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_LATENCY_MS", 20);
    private static readonly int SeedMinutes = CampaignReleaseRig.IntKnob("GOLDPATH_BENCH_CAMPAIGN_SEED_MINUTES", 180);

    private readonly CampaignReleaseRig _rig = new();
    private readonly ITestOutputHelper _output;

    public CampaignFleet02BenchTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
        => Fleet02FactAttribute.Enabled ? _rig.StartAsync(Consumers, LatencyMs) : Task.CompletedTask;

    public Task DisposeAsync() => _rig.DisposeAsync().AsTask();

    private void Line(FormattableString line) => _output.WriteLine("BENCH-CAMPAIGN fleet02: " + line.ToString(CultureInfo.InvariantCulture));

    [Fleet02Fact]
    public async Task One_campaign_over_30M_targets_holds_the_configured_tps_for_the_hour()
    {
        // The dataset must outlast the window, or the last seconds would be empty for want of
        // targets rather than for want of rate.
        Assert.True(Targets >= (long)Tps * Seconds,
            string.Create(CultureInfo.InvariantCulture, $"{Targets} targets cannot feed {Tps} TPS for {Seconds} s"));
        var outDir = Environment.GetEnvironmentVariable("GOLDPATH_BENCH_OUT") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), $"goldpath-fleet02-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}");
        Directory.CreateDirectory(outDir);
        Line($"configured {Tps} TPS (floor {FloorTps}), {Seconds} s, {Targets} targets, {Consumers} consumers (bus knob), target latency {LatencyMs} ms, {Environment.ProcessorCount} logical CPUs, out {outDir}");
        if (FloorTps >= Tps)
        {
            Line($"NOTE the floor ({FloorTps}) is not below the configured rate ({Tps}) and no second may exceed the rate, so a PASS needs every second at exactly {Tps}");
        }

        // 1. Seed: the snapshot runs through the pacer (#288) while a start a day ahead holds the
        //    campaign at Ready (R3.1), so not one item is released before the measured hour.
        var admin = _rig.App.Services;
        Guid campaignId;
        using (var scope = admin.CreateScope())
        {
            var campaign = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "fleet", "s-fleet-02",
                    new Dictionary<string, string> { ["count"] = Targets.ToString(CultureInfo.InvariantCulture) },
                    new GoldpathCampaignPolicy(Tps, null, 100_000, null, null, "UTC") { StartAt = DateTimeOffset.UtcNow.AddDays(1) },
                    tenant: null, actor: "bench", CancellationToken.None);
            campaignId = campaign.Id;
        }

        var seed = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        GoldpathCampaign row;
        while ((row = Row(campaignId)).State != GoldpathCampaignState.Ready)
        {
            Assert.True(row.State is GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating,
                $"the campaign left seeding as {row.State}, not Ready");
            Assert.True(seed.Elapsed < TimeSpan.FromMinutes(SeedMinutes),
                string.Create(CultureInfo.InvariantCulture, $"seeding did not finish within {SeedMinutes} minutes ({row.EnumeratedThrough} of {Targets})"));
            if (seed.Elapsed - lastProgress >= TimeSpan.FromMinutes(5))
            {
                lastProgress = seed.Elapsed;
                Line($"seeding {row.EnumeratedThrough}/{Targets} after {seed.Elapsed.TotalMinutes:F1} min");
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        var seeded = DateTimeOffset.UtcNow - row.CreatedAt;
        Assert.True(row.EnumerationComplete, "Ready without a complete snapshot");
        Assert.Equal(Targets, row.EnumeratedThrough);
        Assert.True(_rig.Clock.Ticks.IsEmpty, "an item was released while the campaign was held at Ready");
        Line($"seeded {Targets} targets in {seeded.TotalSeconds:F1} s = {Targets / seeded.TotalSeconds:F0} rows/s (Created → snapshot complete, not counted)");

        // 2. Start now: clear the start. A pacer tick racing the verb loses it on the row's
        //    concurrency token, so try again rather than fail the night on a race.
        var start = Stopwatch.StartNew();
        while (true)
        {
            using var scope = admin.CreateScope();
            var cleared = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>()
                .ThrottleAsync(campaignId, new GoldpathCampaignThrottle { ClearStartAt = true }, "bench", CancellationToken.None);
            if (cleared.Ok)
            {
                break;
            }

            Assert.True(start.Elapsed < TimeSpan.FromMinutes(1), cleared.Message);
            await Task.Delay(250);
        }

        // 3. The hour opens on the first whole second after the first release.
        long first;
        while (!_rig.Clock.Ticks.TryPeek(out first))
        {
            Assert.True(start.Elapsed < TimeSpan.FromMinutes(3), "no item was released within 3 minutes of the start");
            await Task.Delay(200);
        }

        var measureFrom = new DateTimeOffset(first - (first % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(1);
        var measureTo = measureFrom.AddSeconds(Seconds);
        Line($"start cleared; first release {start.Elapsed.TotalSeconds:F1} s later; measuring {measureFrom:O} → {measureTo:O}");
        var remaining = measureTo - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromSeconds(1));
        }

        // 4. Stop releasing, let claimed work drain, then let the sink's last batch land.
        using (var scope = admin.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>()
                .PauseAsync(campaignId, "bench", CancellationToken.None);
            Assert.True(paused.Ok, paused.Message);
        }

        var inFlight = new[] { GoldpathCampaignItemState.Processing, GoldpathCampaignItemState.AwaitingAck };
        var drain = Stopwatch.StartNew();
        while (_rig.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Any(i => i.CampaignId == campaignId && inFlight.Contains(i.State))))
        {
            Assert.True(drain.Elapsed < TimeSpan.FromMinutes(5), "in-flight items did not drain within 5 minutes of the pause");
            await Task.Delay(1_000);
        }

        var settle = Stopwatch.StartNew();
        long durable;
        while ((row = Row(campaignId)).SucceededCount + row.FailedCount != (durable = Terminal(campaignId))
               && settle.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(500);
        }

        // 5. Judge, against the signed thresholds only.
        var perSecond = new long[Seconds];
        foreach (var at in _rig.Clock.Ticks.Where(t => t >= measureFrom.UtcTicks && t < measureTo.UtcTicks))
        {
            perSecond[(int)((at - measureFrom.UtcTicks) / TimeSpan.TicksPerSecond)]++;
        }

        var perSecondSent = new long[Seconds];
        foreach (var at in _rig.Clock.SendTicks.Where(t => t >= measureFrom.UtcTicks && t < measureTo.UtcTicks))
        {
            perSecondSent[(int)((at - measureFrom.UtcTicks) / TimeSpan.TicksPerSecond)]++;
        }

        var executions = CampaignReleaseRig.SimulatedTarget.Executions;
        var counts = new Fleet02Counts(
            Published: _rig.Clock.Ticks.Count,
            Stranded: _rig.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                .LongCount(i => i.CampaignId == campaignId && i.State == GoldpathCampaignItemState.Released)),
            Durable: durable,
            Sink: row.SucceededCount + row.FailedCount,
            ExecutedTargets: executions.Count,
            ExecutedTwice: executions.Count(e => e.Value > 1));
        var verdict = CampaignFleet02Verdict.Judge(perSecond, Tps, FloorTps, Environment.ProcessorCount, counts);

        Line($"VERDICT {verdict.Label}");
        Line($"mean {verdict.MeanTps:F1}/s (floor {FloorTps}: {(verdict.MeetsMean ? "met" : "missed")}); {verdict.TpsPerCore:F1} TPS per logical CPU; per-second p5 {verdict.RateP5} p50 {verdict.RateP50} p95 {verdict.RateP95}");
        Line($"p95 jitter {100 * verdict.P95Deviation:F2}% (±5%: {(verdict.MeetsJitter ? "met" : "missed")}); windows within ±5% {verdict.WithinBand}/{Seconds}; above the ceiling {verdict.Above}; empty {verdict.Empty}");
        Line($"published {counts.Published}, stranded on the queue by the pause {counts.Stranded}, terminal {counts.Durable}, sink {counts.Sink}, executed {counts.ExecutedTargets} targets, twice {counts.ExecutedTwice}: lost {verdict.Lost}, duplicated {verdict.Duplicated}");
        Line($"at the send instant (diagnostic, not the verdict): above the ceiling {perSecondSent.LongCount(n => n > Tps)}, max {perSecondSent.Max()}; at the publish instant: max {perSecond.Max()}");
        Line($"peak concurrent handler calls {CampaignReleaseRig.SimulatedTarget.PeakConcurrency} (bus knob {Consumers})");

        await File.WriteAllTextAsync(Path.Combine(outDir, "release-per-second.csv"), CampaignFleet02Verdict.ToCsv(perSecond, measureFrom));
        await File.WriteAllTextAsync(Path.Combine(outDir, "send-per-second.csv"), CampaignFleet02Verdict.ToCsv(perSecondSent, measureFrom));
        await File.WriteAllLinesAsync(Path.Combine(outDir, "verdict.txt"), Describe(verdict, counts, measureFrom, seeded));

        // A hardware ceiling publishes (signed point 4); lost, doubled or bursting work never does.
        Assert.NotEqual("FAIL", verdict.Label);
    }

    private GoldpathCampaign Row(Guid id)
        => _rig.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == id));

    private long Terminal(Guid id)
        => _rig.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .LongCount(i => i.CampaignId == id && (i.State == GoldpathCampaignItemState.Succeeded || i.State == GoldpathCampaignItemState.Failed)));

    private static IEnumerable<string> Describe(Fleet02Result v, Fleet02Counts c, DateTimeOffset from, TimeSpan seeded)
    {
        FormattableString[] lines =
        [
            $"label={v.Label}",
            $"window_from_utc={from:O}",
            $"seconds={Seconds}",
            $"configured_tps={Tps}",
            $"floor_tps={FloorTps}",
            $"targets={Targets}",
            $"seeding_seconds={seeded.TotalSeconds:F1}",
            $"mean_tps={v.MeanTps:F2}",
            $"tps_per_logical_cpu={v.TpsPerCore:F2}",
            $"logical_cpus={Environment.ProcessorCount}",
            $"p95_jitter_pct={100 * v.P95Deviation:F3}",
            $"rate_p5={v.RateP5}",
            $"rate_p50={v.RateP50}",
            $"rate_p95={v.RateP95}",
            $"windows_within_band={v.WithinBand}",
            $"windows_above_ceiling={v.Above}",
            $"windows_empty={v.Empty}",
            $"published={c.Published}",
            $"stranded={c.Stranded}",
            $"terminal={c.Durable}",
            $"sink={c.Sink}",
            $"executed_targets={c.ExecutedTargets}",
            $"executed_twice={c.ExecutedTwice}",
            $"lost={v.Lost}",
            $"duplicated={v.Duplicated}",
            $"consumers_knob={Consumers}",
            $"peak_concurrent_handler_calls={CampaignReleaseRig.SimulatedTarget.PeakConcurrency}",
            $"latency_ms={LatencyMs}",
        ];
        return lines.Select(l => l.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// A fact that runs only when <c>GOLDPATH_BENCH_FLEET02=1</c>: S-FLEET-02 seeds 30M targets and
/// releases for an hour, which no default or monthly <c>Category=Bench</c> run can hold. It shows
/// as SKIPPED, never as a pass, when the variable is absent.
/// </summary>
public sealed class Fleet02FactAttribute : FactAttribute
{
    /// <summary>Whether the opt-in variable is set.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("GOLDPATH_BENCH_FLEET02") == "1";

    /// <summary>Skips unless <see cref="Enabled"/>.</summary>
    public Fleet02FactAttribute()
    {
        if (!Enabled)
        {
            Skip = "S-FLEET-02 is opt-in: set GOLDPATH_BENCH_FLEET02=1 (scripts/bench-campaign.sh fleet02 does).";
        }
    }
}

/// <summary>
/// Runs <see cref="CampaignFleet02BenchTests"/> ALONE, like <see cref="CampaignBrokerBenchCollection"/>:
/// xUnit runs each non-parallel collection after the parallel ones and one at a time, so this load
/// overlaps no other bench and its Quartz host meets no other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CampaignFleet02BenchCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "campaign-fleet02-bench";
}
