using System.Collections.Concurrent;
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

namespace Goldpath.Tests.Integration;

/// <summary>
/// The whole release path on real RabbitMQ 4 + PostgreSQL 17, shared by the release benches
/// (<see cref="CampaignBrokerBenchTests"/>, S-FLEET-02a, and <see cref="CampaignFleet02BenchTests"/>,
/// S-FLEET-02): the Quartz-fired pacer with the PACKAGE DEFAULTS (tick, slice, cron), competing
/// item consumers, a simulated target with latency, the batching outcome sink, and a publish
/// observer that times every release. One rig, so the two benches cannot measure different hosts.
/// </summary>
internal sealed class CampaignReleaseRig : IAsyncDisposable
{
    /// <summary>The image tags the rig starts; the headroom sampler finds the containers by them.</summary>
    public const string PostgresImage = "postgres:17-alpine";

    /// <inheritdoc cref="PostgresImage"/>
    public const string RabbitImage = "rabbitmq:4-alpine";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder(RabbitImage).Build();
    private readonly string _fleet = $"bench-{Guid.NewGuid():N}"[..16];
    private WebApplication? _app;

    /// <summary>Every item message's publish instant, in UTC ticks.</summary>
    public ReleaseClock Clock { get; } = new();

    /// <summary>The running host.</summary>
    public WebApplication App => _app ?? throw new InvalidOperationException("The rig has not started.");

    /// <summary>Reads an integer knob from the environment; a missing, unparsable or non-positive value falls back.</summary>
    public static long Knob(string name, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;

    /// <summary>An <see cref="int"/> knob: as <see cref="Knob"/>, clamped so a value past <see cref="int.MaxValue"/> cannot wrap negative.</summary>
    public static int IntKnob(string name, int fallback) => (int)Math.Min(Knob(name, fallback), int.MaxValue);

    /// <summary>Starts both containers and the host, with <paramref name="consumers"/> as the bus-level consumer knob.</summary>
    public async Task StartAsync(int consumers, int latencyMs)
    {
        SimulatedTarget.Reset(latencyMs);
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // an hour's SQL log would drown the numbers
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<FleetTarget>, SimulatedTarget>();
        // The package defaults for LeaderTick, LeadershipSlice and the pacer cron: this is a
        // baseline of what an adopter gets, not of what a tuned rig could do.
        builder.AddGoldpathCampaign<WebApplicationBuilder, CampaignTests.CampDb>(campaign =>
            campaign.AddCampaign<FleetTarget>("fleet", c => c
                .MaxTargets(50_000_000)
                .Targets((_, parameters) => Synthetic(long.Parse(parameters["count"], CultureInfo.InvariantCulture)))));
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddGoldpathCampaign<CampaignTests.CampDb>();
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
            // N competing item consumers on the one endpoint — the bus-level knob (open-threads
            // T48: it is not proven to reach the endpoint, which is why SimulatedTarget measures
            // the peak concurrency it actually saw), through the engine's test hook.
            MassTransitMessagingEngine.TestHooks.Add(messaging, new(null, cfg =>
            {
                cfg.PrefetchCount = consumers;
                cfg.ConcurrentMessageLimit = consumers;
            }));
        });
        builder.AddGoldpathJobs<WebApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>();
        });
        _app = builder.Build();
        _app.Services.GetRequiredService<IBus>().ConnectPublishObserver(Clock);
        await _app.StartAsync();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>Runs one query in its own scope.</summary>
    public T Query<T>(Func<CampaignTests.CampDb, T> query)
    {
        using var scope = App.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    private static async IAsyncEnumerable<FleetTarget> Synthetic(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new FleetTarget(i);
        }

        await Task.CompletedTask;
    }

    /// <summary>One synthetic target.</summary>
    public sealed record FleetTarget(long Id);

    /// <summary>
    /// The simulated target system: answers after a fixed latency, counts every execution, and
    /// records the peak number of calls it served at once — the consumer count the run ACTUALLY
    /// had, which the bus-level knob does not prove (open-threads T48).
    /// </summary>
    public sealed class SimulatedTarget : IGoldpathCampaignItemHandler<FleetTarget>
    {
        private static int _current;
        private static int _peak;

        /// <summary>Executions per target id.</summary>
        public static ConcurrentDictionary<long, int> Executions { get; } = new();

        /// <summary>The target's latency.</summary>
        public static int LatencyMs { get; private set; } = 20;

        /// <summary>The most calls in flight at once since the last <see cref="Reset"/>.</summary>
        public static int PeakConcurrency => Volatile.Read(ref _peak);

        /// <summary>Clears the counters for a new run.</summary>
        public static void Reset(int latencyMs)
        {
            Executions.Clear();
            LatencyMs = latencyMs;
            Volatile.Write(ref _current, 0);
            Volatile.Write(ref _peak, 0);
        }

        /// <inheritdoc/>
        public async Task ExecuteAsync(FleetTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);
            var seen = Volatile.Read(ref _peak);
            while (now > seen)
            {
                var prior = Interlocked.CompareExchange(ref _peak, now, seen);
                if (prior == seen)
                {
                    break;
                }

                seen = prior;
            }

            try
            {
                await Task.Delay(LatencyMs, cancellationToken);
                Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }
    }

    /// <summary>
    /// Stamps every item message as the bus publishes it. The row's <c>ReleasedAt</c> cannot be
    /// the clock: the pacer publishes before it stamps, a fast consumer claims the still-Pending
    /// row first, and the stamp's <c>State == Pending</c> guard then leaves it null.
    /// </summary>
    public sealed class ReleaseClock : IPublishObserver
    {
        /// <summary>Publish instants, in UTC ticks, in completion order: the moment the bus has the message (the release clock).</summary>
        public ConcurrentQueue<long> Ticks { get; } = new();

        /// <summary>
        /// Send instants, in UTC ticks: the moment the pacer hands the message to the bus. Reported
        /// beside <see cref="Ticks"/> so a second above the ceiling can be told apart as the pacer's
        /// release or the broker's acknowledgement bunching completions together.
        /// </summary>
        public ConcurrentQueue<long> SendTicks { get; } = new();

        /// <inheritdoc/>
        public Task PrePublish<T>(PublishContext<T> context)
            where T : class
        {
            if (context.Message is GoldpathCampaignItemMessage)
            {
                SendTicks.Enqueue(DateTimeOffset.UtcNow.UtcTicks);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PostPublish<T>(PublishContext<T> context)
            where T : class
        {
            if (context.Message is GoldpathCampaignItemMessage)
            {
                Ticks.Enqueue(DateTimeOffset.UtcNow.UtcTicks);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task PublishFault<T>(PublishContext<T> context, Exception exception)
            where T : class => Task.CompletedTask;
    }
}
