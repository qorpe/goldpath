using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Continuous leadership (#284, campaign RFC §6 "within ±5% over a minute", scenario S-FLEET-02a): with the
/// PACKAGE DEFAULTS — the leader tick, the leadership slice and the shipped pacer cron, fired by Quartz on a
/// real PostgreSQL store — no stretch of a minute goes without a leader. Every whole 10-second window across
/// more than two slice hand-overs releases within ±5% of the configured rate, and the first tick of a new fire
/// releases no more than one tick's worth (no burst at the boundary). Release instants are taken where the
/// pacer publishes, as S-FLEET-02a's bench takes them, so a consumer's latency cannot move one between windows.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignContinuousLeadershipTests : IAsyncLifetime
{
    private const int Tps = 50;
    private const int WindowSeconds = 10;

    // Two hand-overs of the default one-minute cron inside the measurement, whatever second the first fire lands on.
    private static readonly TimeSpan Measure = TimeSpan.FromSeconds(130);

    public sealed record Device(long Id);

    public sealed class NoopTarget : IGoldpathCampaignItemHandler<Device>
    {
        public Task ExecuteAsync(Device target, GoldpathCampaignItemContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Stamps every item message as the bus publishes it: the pacer's release, not the consumer's execution.</summary>
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

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly ReleaseClock _clock = new();
    private readonly ITestOutputHelper _output;
    private readonly string _fleet = $"lead-{Guid.NewGuid():N}"[..16];
    private IHost _host = null!;

    public CampaignContinuousLeadershipTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<Device>, NoopTarget>();

        // No LeaderTick, LeadershipSlice or pacer cron here: the defaults are what is under test.
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
            campaign.AddCampaign<Device>("fleet", c => c
                .MaxTargets(1_000_000)
                .Targets((_, parameters) => Synthetic(long.Parse(parameters["count"], CultureInfo.InvariantCulture)))));
        builder.Services.WaitForTheBusOnStart();
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddGoldpathCampaign<CampaignTests.CampDb>();
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        });
        builder.AddGoldpathJobs<HostApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>();
        });
        _host = builder.Build();
        _host.Services.GetRequiredService<IBus>().ConnectPublishObserver(_clock);
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Every_ten_second_window_across_slice_hand_overs_releases_within_five_percent_of_the_rate()
    {
        var options = _host.Services.GetRequiredService<GoldpathCampaignOptions>();
        var count = (long)Tps * (long)(Measure.TotalSeconds + 120);   // the campaign outlasts the measurement
        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "fleet", "continuous-leadership",
                    new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) },
                    new GoldpathCampaignPolicy(Tps, null, 100_000, null, null, "UTC"), tenant: null, actor: "test", CancellationToken.None);
        }

        // The measurement opens at the first release, whenever the cron's first fire lands.
        var wait = Stopwatch.StartNew();
        long first;
        while (!_clock.Ticks.TryPeek(out first))
        {
            Assert.True(wait.Elapsed < TimeSpan.FromMinutes(3), "no item was released within 3 minutes of creation");
            await Task.Delay(200);
        }

        var measureFrom = new DateTimeOffset(first - (first % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(1);
        var measureTo = measureFrom + Measure;
        var remaining = measureTo - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining + TimeSpan.FromSeconds(1));
        }

        var released = _clock.Ticks.Where(t => t >= measureFrom.UtcTicks && t < measureTo.UtcTicks).Order().ToList();
        var windows = new long[(int)Measure.TotalSeconds / WindowSeconds];
        foreach (var at in released)
        {
            windows[(int)((at - measureFrom.UtcTicks) / (TimeSpan.TicksPerSecond * WindowSeconds))]++;
        }

        // The longest stretch with no release: a tick's spacing in steady state, the takeover gap at a hand-over.
        var longestGap = released.Zip(released.Skip(1), (a, b) => TimeSpan.FromTicks(b - a)).DefaultIfEmpty().Max();
        var expected = Tps * WindowSeconds;
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"continuous leadership: tick {options.LeaderTick.TotalMilliseconds} ms, slice {options.LeadershipSlice.TotalSeconds} s; {Tps} TPS; per-{WindowSeconds}s windows [{string.Join(", ", windows)}] (expected {expected} ±5%); longest gap between releases {longestGap.TotalMilliseconds:F0} ms"));

        Assert.All(windows, window => Assert.InRange(window, expected * 0.95, expected * 1.05));

        // No burst at a boundary: a tick never releases more than one tick's worth, the first tick of a fire included.
        // Release instants are bucketed on a fixed LeaderTick grid the ticks are not aligned to, so one bucket may hold
        // parts of two ticks; a fire that started on a banked second of tokens would put Tps (four ticks' worth) into one.
        var perTickCeiling = (int)Math.Ceiling(Tps * options.LeaderTick.TotalSeconds);
        var busiestTick = released.GroupBy(t => t / options.LeaderTick.Ticks).Max(g => g.Count());
        Assert.True(busiestTick <= perTickCeiling * 2,
            $"{busiestTick} releases inside one {options.LeaderTick.TotalMilliseconds} ms span; a tick's worth is {perTickCeiling}");
    }

    private static async IAsyncEnumerable<Device> Synthetic(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new Device(i);
        }

        await Task.CompletedTask;
    }
}
