using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R3.1 on real PostgreSQL + RabbitMQ (slice R3-S3, #315): a campaign created with a start about
/// twenty seconds out reports <c>Ready</c>, then <c>Running</c>; no item is claimed before its start, however the
/// broker races the marks; and every item completes exactly once. <c>R3StartTests</c> proves the rule on a stepped
/// clock; this proves it where messages are real.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignR3Tests : IAsyncLifetime
{
    private const int Targets = 200;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private IHost _host = null!;
    private readonly string _fleet = $"r3-{Guid.NewGuid():N}"[..16];   // Quartz's SchedulerRepository is process-global

    public async Task InitializeAsync()
    {
        CampaignTests.WinbackHandler.Executions.Clear();
        CampaignTests.WinbackHandler.Poisoned.Clear();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(
            new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<CampaignTests.CustomerTarget>, CampaignTests.WinbackHandler>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(4);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 500;
            campaign.AddCampaign<CampaignTests.CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .Targets((services, _) => services.GetRequiredService<CampaignTests.CampDb>()
                    .Customers.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                    .AsAsyncEnumerable()));
        });
        builder.Services.WaitForTheBusOnStart();   // a publish right after StartAsync must find its queue bound
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
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
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

    private T Query<T>(Func<CampaignTests.CampDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    [Fact]
    public async Task ScheduledStart_ReadyThenRunning_FirstClaimNoEarlierThanStartAt()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;

        Guid id;
        DateTimeOffset startAt;
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Customers.AddRange(Enumerable.Range(1, Targets).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = $"user{i}@example.test",
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);

            // Whole seconds: the start is compared with what the database stores, never with a sub-tick remainder.
            var now = DateTimeOffset.UtcNow;
            startAt = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddSeconds(20);
            var engine = scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>();
            var policy = new GoldpathCampaignPolicy(100, null, 5_000, null, null, "UTC") { StartAt = startAt };
            id = (await engine.CreateAsync(scope.ServiceProvider, "winback", "scheduled",
                new Dictionary<string, string>(), policy, tenant: null, actor: "it", token)).Id;
        }

        GoldpathCampaign Campaign() => Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == id));

        // Ready: the snapshot is complete and the start is ahead; nothing is released while it is.
        var states = new List<GoldpathCampaignState>();
        while (DateTimeOffset.UtcNow < startAt.AddSeconds(-2))
        {
            var campaign = Campaign();
            states.Add(campaign.State);
            Assert.Equal(0, campaign.ReleasedThrough);
            await Task.Delay(250, token);
        }

        Assert.Equal(GoldpathCampaignState.Ready, states[^1]);
        Assert.Equal(startAt, Campaign().StartAt);

        // Running, then done: every item terminal.
        while (Campaign().State is not (GoldpathCampaignState.Completed or GoldpathCampaignState.CompletedWithFailures))
        {
            await Task.Delay(250, token);
            states.Add(Campaign().State);
        }

        Assert.Contains(GoldpathCampaignState.Running, states);
        Assert.Equal(GoldpathCampaignState.Completed, Campaign().State);
        Assert.True(states.IndexOf(GoldpathCampaignState.Ready) < states.IndexOf(GoldpathCampaignState.Running), "Ready comes before Running");

        // No item was claimed before its start, whatever order the broker delivered the marks in.
        var claims = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == id)
            .Select(i => new { i.Seq, i.ClaimedAt, i.State }).ToList());
        Assert.Equal(Targets, claims.Count);
        Assert.All(claims, c => Assert.True(c.ClaimedAt >= startAt, $"item {c.Seq} claimed at {c.ClaimedAt:O}, before the start {startAt:O}"));
        Assert.All(claims, c => Assert.Equal(GoldpathCampaignItemState.Succeeded, c.State));

        // Every item completed exactly once.
        Assert.Equal(Targets, CampaignTests.WinbackHandler.Executions.Count);
        Assert.All(CampaignTests.WinbackHandler.Executions.Values, count => Assert.Equal(1, count));
    }
}
