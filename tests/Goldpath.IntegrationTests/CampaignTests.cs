using System.Collections.Concurrent;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The L4 winback story on real PostgreSQL + RabbitMQ with the REAL runner: thousands of
/// dormant customers become a paced campaign — the pacer leads across MANY short slices
/// (every hand-over is a takeover resumed from the durable watermarks), a LIVE throttle
/// verb lifts the TPS mid-flight, competing consumers claim-before-execute so nothing
/// double-sends, poisoned targets exhaust into the jobs repair queue, and the
/// replay-items verb heals them once the world is fixed.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignTests : IAsyncLifetime
{
    public sealed record CustomerTarget(int Id, string Email);

    public sealed class Customer
    {
        public int Id { get; set; }
        public string Email { get; set; } = "";
        public DateTimeOffset LastOrderAt { get; set; }
    }

    public sealed class CampDb(DbContextOptions<CampDb> options) : DbContext(options)
    {
        public DbSet<Customer> Customers => Set<Customer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddGoldpathCampaign();
            modelBuilder.AddGoldpathJobs();
        }
    }

    /// <summary>Counts executions per customer; fails the ids scripted into <see cref="Poisoned"/>.</summary>
    public sealed class WinbackHandler : IGoldpathCampaignItemHandler<CustomerTarget>
    {
        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        public static ConcurrentDictionary<int, bool> Poisoned { get; } = new();

        public Task ExecuteAsync(CustomerTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            if (Poisoned.ContainsKey(target.Id))
            {
                throw new InvalidOperationException($"the mail gateway refused customer {target.Id}");
            }

            Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private IHost _host = null!;
    private readonly string _fleet = $"camp-{Guid.NewGuid():N}"[..16];   // unique per test: Quartz's SchedulerRepository is process-global

    public async Task InitializeAsync()
    {
        WinbackHandler.Executions.Clear();
        WinbackHandler.Poisoned.Clear();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampDb(new DbContextOptionsBuilder<CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<CustomerTarget>, WinbackHandler>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampDb>(campaign =>
        {
            // Short slices ON PURPOSE: every cron re-fire is a leader hand-over, so the
            // watermark-takeover path runs many times inside one test.
            campaign.LeadershipSlice = TimeSpan.FromSeconds(3);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 500;
            campaign.AddCampaign<CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .Targets((services, _) => services.GetRequiredService<CampDb>()
                    .Customers.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new CustomerTarget(x.Id, x.Email))
                    .AsAsyncEnumerable()));
        });
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddGoldpathCampaignConsumers<CampDb>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbit.GetConnectionString()));
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());
        builder.AddGoldpathJobs<HostApplicationBuilder, CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampDb>(pacerCron: "0/5 * * * * ?");   // leadership re-guaranteed every 5s
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    private T Query<T>(Func<CampDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampDb>());
    }

    private async Task<GoldpathCampaign> CreateCampaignAsync(int customers, GoldpathCampaignPolicy policy, CancellationToken token)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampDb>();
        db.Customers.AddRange(Enumerable.Range(1, customers).Select(i => new Customer
        {
            Id = i,
            Email = $"user{i}@example.test",
            LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
        }));
        await db.SaveChangesAsync(token);

        return await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampDb>>()
            .CreateAsync(scope.ServiceProvider, "winback", "july winback",
                new Dictionary<string, string>(), policy, tenant: null, actor: "it-operator", token);
    }

    private async Task<GoldpathCampaign> WaitForStateAsync(Guid id, GoldpathCampaignState target, CancellationToken token)
    {
        while (true)
        {
            var row = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == id));
            if (row.State == target)
            {
                return row;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        }
    }

    [Fact]
    public async Task Thousands_release_paced_across_leader_handovers_and_a_live_throttle_lifts_the_ceiling()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        // At 10 TPS, 3000 customers would need ~5 minutes — past the test timeout.
        var campaign = await CreateCampaignAsync(3_000,
            new GoldpathCampaignPolicy(Tps: 10, DailyQuota: null, MaxInFlight: 2_000, null, null, "UTC"), timeout.Token);

        // Wait until the pacer visibly paces (some released, nowhere near all)...
        while (true)
        {
            var row = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaign.Id));
            if (row.ReleasedThrough is > 0 and < 500)
            {
                break;
            }

            Assert.True(row.ReleasedThrough < 500, "10 TPS must not have released 500+ this early — pacing is broken");
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }

        // ...then the operator throttles UP on the LIVE row (D6): no restart, no redeploy.
        using (var scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CampDb>().Set<GoldpathCampaign>()
                .Where(c => c.Id == campaign.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Tps, 100_000), timeout.Token);
        }

        // Completion inside the timeout IS the throttle proof (10 TPS could not get there).
        var done = await WaitForStateAsync(campaign.Id, GoldpathCampaignState.Completed, timeout.Token);
        Assert.Equal(3_000, done.EnumeratedThrough);
        Assert.Equal(3_000, done.ReleasedThrough);
        Assert.Equal(3_000, done.SucceededCount);
        Assert.Equal(0, done.FailedCount);
        Assert.NotNull(done.CompletedAt);

        // EVERY customer exactly once — across many leader hand-overs and broker
        // redeliveries, the claim guard never let a double-send through (constraint 2).
        Assert.Equal(3_000, WinbackHandler.Executions.Count);
        Assert.All(WinbackHandler.Executions, e => Assert.Equal(1, e.Value));
        Assert.Equal(0, Query(db => db.Set<GoldpathCampaignItem>()
            .Count(i => i.State != GoldpathCampaignItemState.Succeeded)));
    }

    [Fact]
    public async Task Poisoned_targets_exhaust_into_the_repair_queue_and_replay_heals_them_after_the_fix()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        WinbackHandler.Poisoned.TryAdd(13, true);
        WinbackHandler.Poisoned.TryAdd(37, true);
        var campaign = await CreateCampaignAsync(50,
            new GoldpathCampaignPolicy(Tps: 1_000, DailyQuota: null, MaxInFlight: 100, null, null, "UTC"), timeout.Token);

        var done = await WaitForStateAsync(campaign.Id, GoldpathCampaignState.CompletedWithFailures, timeout.Token);
        Assert.Equal(48, done.SucceededCount);
        Assert.Equal(2, done.FailedCount);

        // The failed set files into the JOBS repair queue WITH the chunk checkpoint —
        // that write lands when the completing slice ends, so wait for it to surface.
        List<GoldpathJobItemFailure> failures;
        while ((failures = Query(db => db.Set<GoldpathJobItemFailure>().AsNoTracking()
            .Where(f => f.ItemKey.StartsWith(campaign.Id.ToString("N")))
            .ToList())).Count < 2)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }

        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, f => f.ItemKey.EndsWith("#13", StringComparison.Ordinal));
        Assert.All(failures, f => Assert.Contains("the mail gateway refused", f.Reason, StringComparison.Ordinal));

        // Fix the world, then the standard jobs verb replays THROUGH the handler.
        WinbackHandler.Poisoned.Clear();
        var admin = _host.Services.GetRequiredService<GoldpathJobsAdminService<CampDb>>();
        var replayed = await admin.ReplayItemsAsync(failures[0].RunId, "it-operator", timeout.Token);
        Assert.True(replayed.Ok, replayed.Message);

        // The verb fires the replay through the runner — wait for the heal to land. Wait on the
        // OUTCOME, not the claim: a replay takes its item out of FailedCount when it claims it
        // and adds it to SucceededCount only when the outcome applies, so "FailedCount == 0"
        // alone is true while the last replay is still between the two (CI flake #357).
        GoldpathCampaign healed;
        while ((healed = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaign.Id)))
            is not { FailedCount: 0, SucceededCount: 50 })
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        }

        Assert.Equal(0, healed.FailedCount);
        Assert.Equal(50, healed.SucceededCount);
        Assert.Equal(0, Query(db => db.Set<GoldpathCampaignItem>()
            .Count(i => i.State != GoldpathCampaignItemState.Succeeded)));
        Assert.Equal(1, WinbackHandler.Executions[13]);   // the replay executed it — once
    }
}
