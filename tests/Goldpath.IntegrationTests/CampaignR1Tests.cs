using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R1 on real PostgreSQL + RabbitMQ (RFC test plan §R1.2): two
/// campaigns under ONE GlobalTps never jointly exceed it in any one-second window, and a
/// live ExcludedDays throttle halts the pacer on its next tick — then resumes when
/// lifted, without a restart.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignR1Tests : IAsyncLifetime
{
    private const int GlobalTps = 20;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private IHost _host = null!;
    private readonly string _fleet = $"cr1-{Guid.NewGuid():N}"[..16];   // Quartz's SchedulerRepository is process-global

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
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<CampaignTests.CustomerTarget>, CampaignTests.WinbackHandler>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(4);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 500;
            campaign.GlobalTps = GlobalTps;   // R1.4: one bucket over every campaign
            campaign.AddCampaign<CampaignTests.CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .Targets((services, _) => services.GetRequiredService<CampaignTests.CampDb>()
                    .Customers.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                    .AsAsyncEnumerable()));
        });
        builder.Services.WaitForTheBusOnStart();   // a publish right after StartAsync must find its queue bound
        // Through the MassTransit-typed registration ON PURPOSE: the templates write it until preview.11, so one
        // campaign suite proves end to end that it reaches the same handlers (the others use the builder).
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddGoldpathCampaignConsumers<CampaignTests.CampDb>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbit.GetConnectionString()));
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());
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
    public async Task TwoCampaignsUnderOneGlobalTps_NeverJointlyExceedIt_AndALiveExclusionHalts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Customers.AddRange(Enumerable.Range(1, 600).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = $"user{i}@example.test",
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);

            var engine = scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>();
            // Each generous alone: only the GLOBAL bucket can explain a joint ceiling.
            var generous = new GoldpathCampaignPolicy(1_000, null, 5_000, null, null, "UTC");
            await engine.CreateAsync(scope.ServiceProvider, "winback", "push-a",
                new Dictionary<string, string>(), generous, tenant: null, actor: "it", token);
            await engine.CreateAsync(scope.ServiceProvider, "winback", "push-b",
                new Dictionary<string, string>(), generous, tenant: null, actor: "it", token);
        }

        long Released() => Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Sum(c => c.ReleasedThrough));

        // Wait for the pacer to actually start releasing.
        while (Released() == 0)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(200, token);
        }

        // R1.4 (strict since #292): let both campaigns run hungry for a few seconds, then read when
        // every item was released and check each one-second window, at tick granularity, against
        // GlobalTps itself — no tolerance. The first release after the campaigns appear follows a
        // stretch in which the global bucket refilled with nothing to release: the burst case.
        var sampled = 0;
        while (sampled < 6 && !Query(db => db.Set<GoldpathCampaign>().AsNoTracking()
            .All(c => c.ReleasedThrough >= c.EnumeratedThrough && c.EnumerationComplete)))
        {
            await Task.Delay(1_000, token);
            sampled++;
        }

        Assert.True(sampled >= 3, "the run finished before enough windows were sampled — enlarge the source");

        // R1.1 live: excluding TODAY on both campaigns halts release on the next tick…
        using (var scope = _host.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>();
            var today = DateTime.UtcNow.DayOfWeek.ToString();
            foreach (var id in Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Select(c => c.Id).ToList()))
            {
                Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(ExcludedDays: [today]), "it", token)).Ok);
            }
        }

        await Task.Delay(1_500, token);   // let in-flight releases settle
        var halted = Released();
        await Task.Delay(2_000, token);
        Assert.Equal(halted, Released());

        // …and lifting it resumes without any restart.
        using (var scope = _host.Services.CreateScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<GoldpathCampaignAdminService<CampaignTests.CampDb>>();
            foreach (var id in Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Select(c => c.Id).ToList()))
            {
                Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(ClearExcludedDays: true), "it", token)).Ok);
            }
        }

        while (Released() == halted)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(500, token);
        }

        // The halt was an idle stretch too: the global bucket kept refilling while nothing released. Two more seconds of
        // release after the lift, then every one-second window of the whole run — start, halt, resume — is held to
        // GlobalTps itself.
        await Task.Delay(2_000, token);
        var worst = WorstSecondOfTicks(Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.ReleasedAt != null)
            .Select(i => i.ReleasedAt!.Value)
            .ToList()));
        Assert.True(worst.Released <= GlobalTps,
            $"one second of ticks (from {worst.From:HH:mm:ss.fff}) released {worst.Released} > global ceiling {GlobalTps}");
    }

    /// <summary>
    /// The most any one second of pacer ticks released, from the items' release stamps. A tick stamps each campaign's
    /// batch with one instant, so stamps closer than half a tick are one tick; a gap of several ticks counts as the ticks
    /// it spans, so a window never holds more ticks than one second has. A re-released item keeps only its last stamp,
    /// which can hide a release but never invent one.
    /// </summary>
    private static (DateTimeOffset From, int Released) WorstSecondOfTicks(List<DateTimeOffset> stamps)
    {
        var tick = TimeSpan.FromMilliseconds(100);   // the host's LeaderTick
        var perSecond = (int)(TimeSpan.FromSeconds(1).Ticks / tick.Ticks);
        var ticks = new List<(DateTimeOffset At, long Index, int Released)>();
        foreach (var stamp in stamps.Order())
        {
            if (ticks.Count > 0 && stamp - ticks[^1].At < tick / 2)
            {
                ticks[^1] = (ticks[^1].At, ticks[^1].Index, ticks[^1].Released + 1);
                continue;
            }

            var index = ticks.Count == 0 ? 0 : ticks[^1].Index + Math.Max(1, (long)Math.Round((stamp - ticks[^1].At) / tick));
            ticks.Add((stamp, index, 1));
        }

        var worst = (From: DateTimeOffset.MinValue, Released: 0);
        foreach (var first in ticks)
        {
            var released = ticks.Where(t => t.Index >= first.Index && t.Index < first.Index + perSecond).Sum(t => t.Released);
            if (released > worst.Released)
            {
                worst = (first.At, released);
            }
        }

        return worst;
    }
}
