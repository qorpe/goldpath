using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-FLEET-02a (scripts/bench-campaign.sh → ops/campaign-benchmarks.md): ONE campaign at
/// <c>Tps = 1000</c> through the whole release path on real RabbitMQ 4 + PostgreSQL 17 —
/// the Quartz-fired pacer with the PACKAGE DEFAULTS (tick, slice, cron), competing item
/// consumers, a simulated target with latency, and the batching outcome sink. It measures
/// and does not tune: the threshold is expected red on today's release path, and the red
/// result is the baseline Parts 2 and 3 of #283's gap are judged against.
/// <para>
/// Knobs, all environment variables so the script can drive them:
/// <c>GOLDPATH_BENCH_CAMPAIGN_SECONDS</c> (measurement duration, default 900; the smoke is
/// 120), <c>GOLDPATH_BENCH_CAMPAIGN_TPS</c> (default 1000),
/// <c>GOLDPATH_BENCH_CAMPAIGN_CONSUMERS</c> (concurrent item consumers, default 32) and
/// <c>GOLDPATH_BENCH_CAMPAIGN_LATENCY_MS</c> (the target's latency, default 20).
/// </para>
/// </summary>
[Trait("Category", "Bench")]
[Collection("quartz-process-globals")]
public sealed class CampaignBrokerBenchTests : IAsyncLifetime
{
    public sealed record FleetTarget(long Id);

    /// <summary>The simulated target system: answers after a fixed latency and counts every execution.</summary>
    public sealed class SimulatedTarget : IGoldpathCampaignItemHandler<FleetTarget>
    {
        public static ConcurrentDictionary<long, int> Executions { get; } = new();

        public static int LatencyMs { get; set; } = 20;

        public async Task ExecuteAsync(FleetTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(LatencyMs, cancellationToken);
            Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
        }
    }

    /// <summary>
    /// Stamps every item message as the bus publishes it. The row's <c>ReleasedAt</c> cannot be
    /// the clock: the pacer publishes before it stamps, a fast consumer claims the still-Pending
    /// row first, and the stamp's <c>State == Pending</c> guard then leaves it null.
    /// </summary>
    private sealed class ReleaseClock : IPublishObserver
    {
        public ConcurrentQueue<long> Ticks { get; } = new();

        public Task PrePublish<T>(PublishContext<T> context)
            where T : class => Task.CompletedTask;

        public Task PostPublish<T>(PublishContext<T> context)
            where T : class
        {
            if (context.Message is GoldpathCampaignItemMessage)
            {
                Ticks.Enqueue(DateTimeOffset.UtcNow.UtcTicks);
            }

            return Task.CompletedTask;
        }

        public Task PublishFault<T>(PublishContext<T> context, Exception exception)
            where T : class => Task.CompletedTask;
    }

    private readonly ReleaseClock _clock = new();

    private static readonly int Seconds = Knob("GOLDPATH_BENCH_CAMPAIGN_SECONDS", 900);
    private static readonly int Tps = Knob("GOLDPATH_BENCH_CAMPAIGN_TPS", 1_000);
    private static readonly int Consumers = Knob("GOLDPATH_BENCH_CAMPAIGN_CONSUMERS", 32);
    private static readonly int LatencyMs = Knob("GOLDPATH_BENCH_CAMPAIGN_LATENCY_MS", 20);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly ITestOutputHelper _output;
    private readonly string _fleet = $"bench-{Guid.NewGuid():N}"[..16];
    private WebApplication _app = null!;

    public CampaignBrokerBenchTests(ITestOutputHelper output) => _output = output;

    private static int Knob(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    public async Task InitializeAsync()
    {
        SimulatedTarget.Executions.Clear();
        SimulatedTarget.LatencyMs = LatencyMs;
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // a 15-minute run's SQL log would drown the numbers
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<FleetTarget>, SimulatedTarget>();
        // The package defaults for LeaderTick, LeadershipSlice and the pacer cron: this is a
        // baseline of what an adopter gets, not of what a tuned rig could do.
        builder.AddGoldpathCampaign<WebApplicationBuilder, CampaignTests.CampDb>(campaign =>
            campaign.AddCampaign<FleetTarget>("fleet", c => c
                .MaxTargets(50_000_000)
                .Targets((_, parameters) => Synthetic(long.Parse(parameters["count"], CultureInfo.InvariantCulture)))));
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddGoldpathCampaignConsumers<CampaignTests.CampDb>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbit.GetConnectionString()));
                // N competing item consumers on the one endpoint.
                cfg.PrefetchCount = Consumers;
                cfg.ConcurrentMessageLimit = Consumers;
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());
        builder.AddGoldpathJobs<WebApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>();
        });
        _app = builder.Build();
        _app.Services.GetRequiredService<IBus>().ConnectPublishObserver(_clock);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    private T Query<T>(Func<CampaignTests.CampDb, T> query)
    {
        using var scope = _app.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    [Fact]
    public async Task One_leader_sustains_the_configured_tps_on_a_real_broker()
    {
        // Enough targets to keep the pacer busy for the whole window even at the configured rate.
        var count = (long)Tps * Seconds;
        Guid campaignId;
        using (var scope = _app.Services.CreateScope())
        {
            var campaign = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "fleet", "s-fleet-02a",
                    new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) },
                    new GoldpathCampaignPolicy(Tps, null, 100_000, null, null, "UTC"), tenant: null, actor: "bench", CancellationToken.None);
            campaignId = campaign.Id;
        }

        // The window opens at the first release, whenever the cron's first fire lands.
        var wait = Stopwatch.StartNew();
        long first;
        while (!_clock.Ticks.TryPeek(out first))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromMinutes(3), "no item was released within 3 minutes of creation");
            await Task.Delay(200);
        }

        var measureFrom = new DateTimeOffset(first - (first % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(1);
        var measureTo = measureFrom.AddSeconds(Seconds);
        var remaining = measureTo - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromSeconds(1));
        }

        // Stop releasing, then let what is in flight drain (R2 review #8) before reconciling.
        using (var scope = _app.Services.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>()
                .PauseAsync(campaignId, "bench", CancellationToken.None);
            Assert.True(paused.Ok, paused.Message);
        }

        // Claimed work drains; a RELEASED item still on the queue is dropped by its consumer
        // once the campaign is paused (claim refuses), so it stays Released and never executes.
        var inFlight = new[] { GoldpathCampaignItemState.Processing, GoldpathCampaignItemState.AwaitingAck };
        var drain = Stopwatch.StartNew();
        while (Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                   .Any(i => i.CampaignId == campaignId && inFlight.Contains(i.State))))
        {
            Assert.True(drain.Elapsed < TimeSpan.FromMinutes(5), "in-flight items did not drain within 5 minutes of the pause");
            await Task.Delay(1_000);
        }

        // The outcome sink batches: give its last batch a moment to land on the campaign row.
        var settle = Stopwatch.StartNew();
        GoldpathCampaign row;
        long durable;
        while ((row = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).SucceededCount + row.FailedCount
               != (durable = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                   .LongCount(i => i.CampaignId == campaignId && (i.State == GoldpathCampaignItemState.Succeeded || i.State == GoldpathCampaignItemState.Failed))))
               && settle.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(500);
        }

        // Released items per 1-second window, over whole seconds inside the measurement window.
        var released = _clock.Ticks.Where(t => t >= measureFrom.UtcTicks && t < measureTo.UtcTicks).ToList();
        var perSecond = new long[Seconds];
        foreach (var at in released)
        {
            perSecond[(int)((at - measureFrom.UtcTicks) / TimeSpan.TicksPerSecond)]++;
        }

        var sorted = perSecond.Order().ToArray();
        var withinBand = perSecond.Count(n => Math.Abs(n - Tps) <= Tps * 0.05);
        var above = perSecond.Count(n => n > Tps);
        var empty = perSecond.Count(n => n == 0);
        var executions = SimulatedTarget.Executions;
        var queued = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .LongCount(i => i.CampaignId == campaignId && i.State == GoldpathCampaignItemState.Released));
        var twice = executions.Count(e => e.Value > 1);

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-CAMPAIGN broker: configured {Tps} TPS, {Seconds} s, {Consumers} consumers, target latency {LatencyMs} ms, {Environment.ProcessorCount} logical CPUs"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-CAMPAIGN broker: published {released.Count} item messages in the window = {released.Count / (double)Seconds:F1}/s mean; per-second p50 {Percentile(sorted, 0.50)} p95 {Percentile(sorted, 0.95)} min {sorted[0]} max {sorted[^1]}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-CAMPAIGN broker: windows within ±5%: {withinBand}/{Seconds} = {100.0 * withinBand / Seconds:F1}%; above the ceiling: {above}; empty: {empty}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-CAMPAIGN broker: reconciliation sink {row.SucceededCount + row.FailedCount} (ok {row.SucceededCount}, failed {row.FailedCount}) / durable {durable} / handler executions {executions.Values.Sum()} over {executions.Count} targets, executed twice {twice}; released but never claimed at the pause {queued}; published in all {_clock.Ticks.Count}"));

        // Exactly-once first: a rate number over lost or doubled work would mean nothing.
        Assert.Equal(durable, row.SucceededCount + row.FailedCount);
        Assert.Equal(durable, executions.Values.Sum());
        Assert.Equal(0, twice);

        // S-FLEET-02a's thresholds: at least 95% of one-second windows within ±5%, none above.
        Assert.True(withinBand >= Seconds * 0.95,
            string.Create(CultureInfo.InvariantCulture, $"only {withinBand}/{Seconds} one-second windows were within ±5% of {Tps} TPS"));
        Assert.Equal(0, above);
    }

    private static long Percentile(long[] sorted, double p)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

    private static async IAsyncEnumerable<FleetTarget> Synthetic(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new FleetTarget(i);
        }

        await Task.CompletedTask;
    }
}
