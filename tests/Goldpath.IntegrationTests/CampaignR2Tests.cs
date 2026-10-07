using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign revision R2.1 on real PostgreSQL + RabbitMQ + the HTTP callback surface: a
/// target system that ACCEPTS and answers later. Odd customers are accepted and a
/// provider loop posts their callbacks over HTTP (which publishes the outcome onto the
/// real bus for the batching sink); two of them are never answered, so the ack sweep
/// turns them into retryable failures and the ladder's second attempt succeeds.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignR2Tests : IAsyncLifetime
{
    /// <summary>Accepted on a first attempt for odd ids; Succeeded otherwise.</summary>
    public sealed class AckHandler : IGoldpathCampaignActionHandler<CampaignTests.CustomerTarget>
    {
        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        public static ConcurrentDictionary<int, bool> NeverAnswered { get; } = new();

        public Task<GoldpathCampaignActionResult> ExecuteAsync(
            CampaignTests.CustomerTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            return Task.FromResult(context.Attempt == 1 && target.Id % 2 == 1
                ? GoldpathCampaignActionResult.Accepted(
                    string.Create(CultureInfo.InvariantCulture, $"corr-{target.Id}-{context.Attempt}"), TimeSpan.FromSeconds(4))
                : GoldpathCampaignActionResult.Succeeded());
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly string _fleet = $"r2-{Guid.NewGuid():N}"[..16];

    public async Task InitializeAsync()
    {
        AckHandler.Executions.Clear();
        AckHandler.NeverAnswered.Clear();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignActionHandler<CampaignTests.CustomerTarget>, AckHandler>();
        builder.AddGoldpathCampaign<WebApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(3);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 500;
            campaign.RetryBackoff = [TimeSpan.FromSeconds(1)];
            campaign.RetryJitter = 0;
            campaign.AddCampaign<CampaignTests.CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .Targets((services, _) => services.GetRequiredService<CampaignTests.CampDb>()
                    .Customers.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                    .AsAsyncEnumerable()));
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
        builder.AddGoldpathJobs<WebApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
        });
        _app = builder.Build();
        _app.MapGoldpathCampaignCallbacks<CampaignTests.CampDb>(exposeUnsecured: true);   // behind mTLS in a real deployment
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
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

    private string Dump(Guid campaignId)
    {
        var campaign = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId));
        var items = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).OrderBy(i => i.Seq).ToList());
        var histogram = string.Join(", ", items.GroupBy(i => i.State).Select(g => $"{g.Key}={g.Count()}"));
        var stuck = string.Join("\n", items.Where(i => i.State != GoldpathCampaignItemState.Succeeded)
            .Select(i => $"  #{i.Seq} {i.State} attempts={i.Attempts} code={i.ErrorCode} err={i.Error} corr={i.CorrelationId} ack={i.AckDeadline:O} next={i.NextAttemptAt:O} released={i.ReleasedAt:O} claimed={i.ClaimedAt:O}"));
        return $"campaign {campaign.State} enumerated={campaign.EnumeratedThrough} complete={campaign.EnumerationComplete} released={campaign.ReleasedThrough} ok={campaign.SucceededCount} failed={campaign.FailedCount} key={campaign.EnumeratedKey}\nitems: {histogram}\n{stuck}";
    }

    [Fact]
    public async Task Accepted_items_settle_through_HTTP_callbacks_and_unanswered_ones_walk_the_ladder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        AckHandler.NeverAnswered.TryAdd(7, true);
        AckHandler.NeverAnswered.TryAdd(21, true);

        Guid campaignId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Customers.AddRange(Enumerable.Range(1, 40).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = $"user{i}@example.test",
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);
            var campaign = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "winback", "async push", new Dictionary<string, string>(),
                    new GoldpathCampaignPolicy(1_000, null, 100, null, null, "UTC") { MaxAttempts = 2 },
                    tenant: null, actor: "it-operator", token);
            campaignId = campaign.Id;
        }

        // The target system: answers every acceptance it can see, except the two scripted silences.
        var statuses = new ConcurrentBag<int>();
        using var providerStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var provider = Task.Run(async () =>
        {
            while (!providerStop.IsCancellationRequested)
            {
                var pending = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
                    .Where(i => i.CampaignId == campaignId && i.State == GoldpathCampaignItemState.AwaitingAck && i.CorrelationId != null)
                    .Select(i => i.CorrelationId!)
                    .ToList());
                foreach (var correlation in pending)
                {
                    var id = int.Parse(correlation.Split('-')[1], CultureInfo.InvariantCulture);
                    if (AckHandler.NeverAnswered.ContainsKey(id))
                    {
                        continue;
                    }

                    var response = await _client.PostAsJsonAsync($"/goldpath/campaign/callbacks/winback/{correlation}", new { succeeded = true }, providerStop.Token);
                    statuses.Add((int)response.StatusCode);
                }

                await Task.Delay(300, providerStop.Token);
            }
        }, providerStop.Token);

        GoldpathCampaign done;
        while ((done = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).State != GoldpathCampaignState.Completed)
        {
            Assert.True(done.State != GoldpathCampaignState.CompletedWithFailures, Dump(campaignId));
            Assert.True(!token.IsCancellationRequested, "timed out:\n" + Dump(campaignId));
            await Task.Delay(500, CancellationToken.None);
        }

        providerStop.Cancel();
        try
        {
            await provider;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Equal(40, done.SucceededCount);
        Assert.Equal(0, done.FailedCount);
        Assert.Equal(40, AckHandler.Executions.Count);
        Assert.All(AckHandler.Executions.Where(e => e.Key is not (7 or 21)), e => Assert.Equal(1, e.Value));
        Assert.Equal(2, AckHandler.Executions[7]);    // ACK_TIMEOUT → rung one → second attempt succeeded
        Assert.Equal(2, AckHandler.Executions[21]);
        var items = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).ToList());
        Assert.All(items, i => Assert.Equal(GoldpathCampaignItemState.Succeeded, i.State));
        // Attempts counts the FAILED attempts on the ladder (R1.3): one ack timeout, then the success.
        Assert.Equal(1, items.Single(i => i.Seq == 7).Attempts);
        Assert.Equal(0, items.Single(i => i.Seq == 8).Attempts);
        Assert.Contains(202, statuses);   // a pending acceptance → outcome published

        // The provider retries a webhook it already delivered: found, settled, nothing published.
        var repeat = await _client.PostAsJsonAsync("/goldpath/campaign/callbacks/winback/corr-1-1", new { succeeded = true }, token);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var unknown = await _client.PostAsJsonAsync("/goldpath/campaign/callbacks/winback/corr-nope", new { succeeded = true }, token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(40, Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId)).SucceededCount);
    }
}
