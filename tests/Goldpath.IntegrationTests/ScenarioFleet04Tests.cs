using System.Collections.Concurrent;
using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-FLEET-04 — pause stops every worker within three seconds (campaign RFC R2.6 "pause means
/// now", on a real broker). Two hosts share one RabbitMQ 4 item queue and one PostgreSQL 17
/// store: a LEADER (pacer, enumeration, consumers) and a WORKER (consumers only), four consumer
/// slots each, a target that answers after 600 ms, and a queue at least ten times deeper than
/// the eight slots when the operator pauses through the admin verb. Every item message still
/// on the queue at the pause must reach a consumer that claims nothing; only the calls already
/// in flight finish. Red on the claim guard before R2.6 (item state only): the backlog keeps
/// draining for as long as the queue is deep.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class ScenarioFleet04Tests : IAsyncLifetime
{
    private const int SlotsPerHost = 4;
    private const int Hosts = 2;
    private const int TotalSlots = SlotsPerHost * Hosts;
    private const int LatencyMs = 600;
    private const int Targets = 400;

    /// <summary>The acceptance number: no claim later than the pause commit plus three seconds.</summary>
    private static readonly TimeSpan Bar = TimeSpan.FromSeconds(3);

    public sealed record FleetDevice(int Id);

    /// <summary>Which host a handler call ran on (one singleton per host).</summary>
    public sealed record HostTag(string Name);

    /// <summary>One handler call: where it ran, when it started (after the claim) and when it returned.</summary>
    public sealed record Call(int Device, string Host, DateTimeOffset Started, DateTimeOffset Ended);

    /// <summary>The simulated device: answers after <see cref="LatencyMs"/> and records every call.</summary>
    public sealed class SlowDevice(HostTag host) : IGoldpathCampaignItemHandler<FleetDevice>
    {
        public static ConcurrentQueue<Call> Calls { get; } = new();

        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        private static int _inFlight;
        private static int _peak;

        /// <summary>The most calls that ever ran at once, across both hosts.</summary>
        public static int Peak => Volatile.Read(ref _peak);

        public async Task ExecuteAsync(FleetDevice target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            var started = DateTimeOffset.UtcNow;
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _peak, now);
            try
            {
                await Task.Delay(LatencyMs, cancellationToken);
                Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                Calls.Enqueue(new Call(target.Id, host.Name, started, DateTimeOffset.UtcNow));
            }
        }

        public static void Reset()
        {
            Calls.Clear();
            Executions.Clear();
            _inFlight = 0;
            _peak = 0;
        }

        private static void InterlockedMax(ref int location, int value)
        {
            int seen;
            while (value > (seen = Volatile.Read(ref location)) && Interlocked.CompareExchange(ref location, value, seen) != seen)
            {
            }
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly List<IHost> _hosts = [];
    private readonly ITestOutputHelper _output;

    public ScenarioFleet04Tests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        SlowDevice.Reset();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }

        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>
    /// A host with <see cref="SlotsPerHost"/> competing item consumers. The leader also runs the
    /// pacer; the worker is what an adopter scales out — consumers and the outcome sink only.
    /// </summary>
    private IHost BuildHost(string name, bool leader)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddSingleton(new HostTag(name));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<FleetDevice>, SlowDevice>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(3);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 100;
            campaign.AddCampaign<FleetDevice>("fleet-pause", c => c
                .MaxTargets(10_000)
                .Targets((_, _) => Devices()));
        });
        builder.Services.WaitForTheBusOnStart();
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddGoldpathCampaignConsumers<CampaignTests.CampDb>();
            // The slots: at most this many item messages handled at once per host. On the item
            // CONSUMER, not the endpoint: the package puts the item consumer and the outcome sink
            // on one endpoint (both are named after the DbContext), and the sink's batch needs its
            // own concurrency. A bus-level limit would not reach it either way.
            bus.AddConsumer<GoldpathCampaignItemConsumer<CampaignTests.CampDb>>((_, consumer) => consumer.ConcurrentMessageLimit = SlotsPerHost);
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbit.GetConnectionString()));
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());
        if (leader)
        {
            builder.AddGoldpathJobs<HostApplicationBuilder, CampaignTests.CampDb>(jobs =>
            {
                jobs.ConnectionName = "campdb";
                jobs.SchedulerName = $"fleet04-{Guid.NewGuid():N}"[..16];
                jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
            });
        }

        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }

    private static async IAsyncEnumerable<FleetDevice> Devices()
    {
        for (var i = 1; i <= Targets; i++)
        {
            yield return new FleetDevice(i);
        }

        await Task.CompletedTask;
    }

    private T Query<T>(IHost host, Func<CampaignTests.CampDb, T> query)
    {
        using var scope = host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    private string Dump(IHost host, Guid campaignId)
    {
        var campaign = Query(host, db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId));
        var states = Query(host, db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId)
            .GroupBy(i => i.State).Select(g => new { g.Key, Count = g.Count() }).ToList());
        return string.Create(CultureInfo.InvariantCulture,
            $"campaign {campaign.State} enumerated={campaign.EnumeratedThrough} released={campaign.ReleasedThrough} ok={campaign.SucceededCount} items [{string.Join(", ", states.Select(s => $"{s.Key}={s.Count}"))}] calls={SlowDevice.Calls.Count} hosts=[{string.Join(",", SlowDevice.Calls.Select(c => c.Host).Distinct())}]");
    }

    [Fact]
    public async Task Pause_stops_every_worker_within_three_seconds_and_resume_completes_every_item_exactly_once()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var leader = BuildHost("leader", leader: true);
        var worker = BuildHost("worker", leader: false);
        await Task.WhenAll(leader.StartAsync(token), worker.StartAsync(token));

        Guid campaignId;
        using (var scope = leader.Services.CreateScope())
        {
            // Tps far above what eight 600 ms slots drain (~13/s): the queue deepens at once.
            campaignId = (await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "fleet-pause", "s-fleet-04", new Dictionary<string, string>(),
                    new GoldpathCampaignPolicy(200, null, 5_000, null, null, "UTC"), tenant: null, actor: "it-operator", token)).Id;
        }

        // Wait for a queue at least 20x the slots deep (the bar is 10x; the margin keeps the
        // red side red) while both hosts are executing.
        long backlog;
        while ((backlog = Query(leader, db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                   .LongCount(i => i.CampaignId == campaignId && i.State == GoldpathCampaignItemState.Released))) < 20 * TotalSlots
               || SlowDevice.Calls.Select(c => c.Host).Distinct().Count() < Hosts)
        {
            Assert.False(token.IsCancellationRequested, $"the backlog never reached {20 * TotalSlots} on both hosts (at {backlog}): {Dump(leader, campaignId)}");
            await Task.Delay(50, CancellationToken.None);
        }

        // The pause, through the admin verb. The instant read BEFORE the call is a lower bound
        // on the commit, so every bound below is measured from the earliest the pause could land.
        var pausedAt = DateTimeOffset.UtcNow;
        using (var scope = leader.Services.CreateScope())
        {
            var paused = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>()
                .PauseAsync(campaignId, "it-operator", token);
            Assert.True(paused.Ok, paused.Message);
        }

        // Watch well past the bar: a claim guard that ignores the pause keeps draining a queue
        // this deep for ~12 s, so 8 s of observation separates the two clearly.
        await Task.Delay(Bar + TimeSpan.FromSeconds(5), token);

        // Snapshot BEFORE resume: a resumed item is claimed again and its ClaimedAt moves.
        var items = Query(leader, db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).ToList());
        var calls = SlowDevice.Calls.ToArray();
        var lateClaims = items.Where(i => i.ClaimedAt > pausedAt + Bar).OrderBy(i => i.ClaimedAt).ToList();
        var lastClaim = items.Where(i => i.ClaimedAt is not null).Max(i => i.ClaimedAt!.Value);
        var lastStart = calls.Max(c => c.Started);
        var quietAt = calls.Max(c => c.Ended);
        var inFlightAtPause = calls.Count(c => c.Started <= pausedAt && c.Ended > pausedAt);
        var stillQueued = items.Count(i => i.State == GoldpathCampaignItemState.Released);
        var pauseAudit = Query(leader, db => db.Set<GoldpathCampaignAudit>().AsNoTracking()
            .Single(a => a.CampaignId == campaignId && a.Action == "pause"));

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"S-FLEET-04: {Hosts} hosts x {SlotsPerHost} slots, target latency {LatencyMs} ms; backlog at the pause {backlog} ({backlog / (double)TotalSlots:F1}x the slots); still queued after the pause {stillQueued}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"S-FLEET-04: pause commit (audit) +{(pauseAudit.At - pausedAt).TotalMilliseconds:F0} ms; last claim {(lastClaim - pausedAt).TotalMilliseconds:F0} ms; last handler start {(lastStart - pausedAt).TotalMilliseconds:F0} ms; last call returned {(quietAt - pausedAt).TotalMilliseconds:F0} ms; in flight at the pause {inFlightAtPause}; max in flight ever {SlowDevice.Peak}; late claims {lateClaims.Count}"));

        Assert.True(lateClaims.Count == 0, string.Create(CultureInfo.InvariantCulture,
            $"{lateClaims.Count} items were claimed later than the pause + {Bar.TotalSeconds} s; the first at +{((lateClaims.FirstOrDefault()?.ClaimedAt ?? pausedAt) - pausedAt).TotalMilliseconds:F0} ms"));
        Assert.True(lastStart <= pausedAt + Bar, $"a handler call started {(lastStart - pausedAt).TotalMilliseconds:F0} ms after the pause");
        Assert.True(inFlightAtPause <= TotalSlots, $"{inFlightAtPause} calls were in flight at the pause, more than the {TotalSlots} slots");
        Assert.True(SlowDevice.Peak <= TotalSlots, $"{SlowDevice.Peak} calls ran at once, more than the {TotalSlots} slots");
        Assert.True(stillQueued >= 10 * TotalSlots, $"only {stillQueued} items were left unclaimed — the queue was not deep enough to prove anything");

        // Resume: every refused message comes back, and every device is called exactly once.
        using (var scope = leader.Services.CreateScope())
        {
            var resumed = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>()
                .ResumeAsync(campaignId, "it-operator", token);
            Assert.True(resumed.Ok, resumed.Message);
        }

        GoldpathCampaign done;
        while ((done = Query(leader, db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).State != GoldpathCampaignState.Completed)
        {
            Assert.True(done.State != GoldpathCampaignState.CompletedWithFailures, $"campaign ended {done.State}: ok={done.SucceededCount} failed={done.FailedCount}");
            Assert.False(token.IsCancellationRequested, $"timed out after resume: {done.State} released={done.ReleasedThrough} ok={done.SucceededCount} failed={done.FailedCount}");
            await Task.Delay(500, CancellationToken.None);
        }

        Assert.Equal(Targets, done.SucceededCount);
        Assert.Equal(0, done.FailedCount);
        Assert.Equal(Targets, SlowDevice.Executions.Count);
        Assert.All(SlowDevice.Executions, e => Assert.Equal(1, e.Value));
        Assert.Equal(Hosts, SlowDevice.Calls.Select(c => c.Host).Distinct().Count());
    }
}
