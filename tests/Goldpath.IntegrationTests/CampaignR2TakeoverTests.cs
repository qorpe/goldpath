using System.Collections.Concurrent;
using System.Globalization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R2.7 on real PostgreSQL + RabbitMQ: a KEYSET campaign type whose
/// leader is stopped ungracefully mid-enumeration. A second host, a different scheduler,
/// takes the campaign over from the row: the selector is reopened AFTER the last
/// materialized key (never from the start with a skip), the item sequence stays dense,
/// and every customer executes exactly once.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignR2TakeoverTests : IAsyncLifetime
{
    public sealed class CountingHandler : IGoldpathCampaignItemHandler<CampaignTests.CustomerTarget>
    {
        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        public Task ExecuteAsync(CampaignTests.CustomerTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>Every key the selector was opened after (null = a fresh start).</summary>
    public static ConcurrentQueue<string?> Openings { get; } = new();

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly List<IHost> _hosts = [];

    public async Task InitializeAsync()
    {
        CountingHandler.Executions.Clear();
        Openings.Clear();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
                // the first host was stopped ungracefully on purpose
            }

            host.Dispose();
        }

        QuartzProcessGlobals.Pin();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    private IHost BuildHost(string fleet)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<CampaignTests.CustomerTarget>, CountingHandler>();
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(3);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 100;
            campaign.OrphanReleaseAfter = TimeSpan.FromSeconds(15);   // R2.6 on a real broker: a release the dead host swallowed comes back
            campaign.AddCampaign<CampaignTests.CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .TargetsAfter((services, _, after) =>
                {
                    Openings.Enqueue(after);
                    var from = after is null ? 0 : int.Parse(after, CultureInfo.InvariantCulture);
                    return Slowly(services.GetRequiredService<CampaignTests.CampDb>()
                        .Customers.AsNoTracking()
                        .Where(x => x.Id > from)
                        .OrderBy(x => x.Id)
                        .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                        .AsAsyncEnumerable());
                }, target => target.Id.ToString(CultureInfo.InvariantCulture)));
        });
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
            jobs.SchedulerName = fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
        });
        var host = builder.Build();
        _hosts.Add(host);
        return host;
    }

    /// <summary>Slow ON PURPOSE: enumeration must still be running when the leader dies.</summary>
    private static async IAsyncEnumerable<CampaignTests.CustomerTarget> Slowly(IAsyncEnumerable<CampaignTests.CustomerTarget> source)
    {
        await foreach (var target in source)
        {
            await Task.Delay(4);
            yield return target;
        }
    }

    private T Query<T>(IHost host, Func<CampaignTests.CampDb, T> query)
    {
        using var scope = host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>());
    }

    private string Dump(IHost host, Guid campaignId)
    {
        var campaign = Query(host, db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId));
        var items = Query(host, db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).OrderBy(i => i.Seq).ToList());
        var histogram = string.Join(", ", items.GroupBy(i => i.State).Select(g => $"{g.Key}={g.Count()}"));
        var stuck = string.Join("\n", items.Where(i => i.State != GoldpathCampaignItemState.Succeeded)
            .Select(i => $"  #{i.Seq} {i.State} attempts={i.Attempts} code={i.ErrorCode} err={i.Error} corr={i.CorrelationId} ack={i.AckDeadline:O} next={i.NextAttemptAt:O} released={i.ReleasedAt:O} claimed={i.ClaimedAt:O}"));
        return $"campaign {campaign.State} enumerated={campaign.EnumeratedThrough} complete={campaign.EnumerationComplete} released={campaign.ReleasedThrough} ok={campaign.SucceededCount} failed={campaign.FailedCount} key={campaign.EnumeratedKey}\nitems: {histogram}\n{stuck}";
    }

    [Fact]
    public async Task An_ungraceful_leader_stop_mid_enumeration_is_resumed_by_key_with_nothing_lost_or_doubled()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var token = timeout.Token;
        var first = BuildHost($"take-a-{Guid.NewGuid():N}"[..16]);
        await first.StartAsync(token);

        Guid campaignId;
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Customers.AddRange(Enumerable.Range(1, 1_500).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = $"user{i}@example.test",
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);
            campaignId = (await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "winback", "keyed push", new Dictionary<string, string>(),
                    new GoldpathCampaignPolicy(500, null, 5_000, null, null, "UTC"), tenant: null, actor: "it-operator", token)).Id;
        }

        // Wait until the first leader is visibly mid-enumeration.
        GoldpathCampaign atStop;
        while ((atStop = Query(first, db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).EnumeratedThrough < 300)
        {
            Assert.False(atStop.EnumerationComplete, "enumeration finished before the leader could be stopped — slow the selector down");
            await Task.Delay(100, token);
        }

        Assert.NotNull(atStop.EnumeratedKey);

        // The ungraceful stop: an already-cancelled token means no graceful drain — the
        // slice is cut where it stands and the leader's in-memory state is gone. The host
        // is then disposed at once, as a killed process would be: its broker connection
        // drops and RabbitMQ requeues whatever its consumers had prefetched. (Leaving the
        // half-stopped host alive instead makes ZOMBIE consumers that hold prefetched
        // messages forever — the orphan and stale-claim sweeps are the answer to that, on
        // their own clocks; this test is about the leader.)
        // The broker connection goes first, as it does when a process dies: a host whose
        // bus is left half-alive keeps ZOMBIE consumers that swallow messages — the
        // outcome sink of the dead host would eat the live host's outcomes.
        await first.Services.GetRequiredService<IBusControl>().StopAsync(token);
        try
        {
            await first.StopAsync(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException)
        {
        }

        _hosts.Remove(first);
        QuartzProcessGlobals.Pin();   // Quartz's process-global log provider must not point at the host being torn down
        first.Dispose();
        QuartzProcessGlobals.Pin();

        var second = BuildHost($"take-b-{Guid.NewGuid():N}"[..16]);
        await second.StartAsync(token);

        GoldpathCampaign done;
        while ((done = Query(second, db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).State != GoldpathCampaignState.Completed)
        {
            Assert.True(done.State != GoldpathCampaignState.CompletedWithFailures, Dump(second, campaignId));
            Assert.True(!token.IsCancellationRequested, "timed out:\n" + Dump(second, campaignId));
            await Task.Delay(500, CancellationToken.None);
        }

        Assert.Equal(1_500, done.EnumeratedThrough);
        Assert.Equal("1500", done.EnumeratedKey);
        Assert.Equal(1_500, done.SucceededCount);
        Assert.Equal(0, done.FailedCount);

        // Dense, unique, every customer exactly once.
        var items = Query(second, db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).ToList());
        Assert.Equal(1_500, items.Count);
        Assert.Equal(1_500, items.Select(i => i.TargetJson).Distinct().Count());
        Assert.Equal(1_500, CountingHandler.Executions.Count);
        Assert.All(CountingHandler.Executions, e => Assert.Equal(1, e.Value));

        // The takeover opened the selector AFTER a key at or past where the first leader
        // was stopped — and never again from the start.
        var openings = Openings.ToArray();
        Assert.Null(openings[0]);
        Assert.True(openings.Length >= 2, "the second host never reopened the selector");
        Assert.All(openings.Skip(1), key => Assert.NotNull(key));
        Assert.True(int.Parse(openings[^1]!, CultureInfo.InvariantCulture) >= atStop.EnumeratedThrough,
            $"the last opening was after key {openings[^1]}, but the row already carried {atStop.EnumeratedThrough} at the stop");
    }
}
