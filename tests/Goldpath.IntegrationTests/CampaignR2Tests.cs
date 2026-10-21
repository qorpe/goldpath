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
/// Campaign revision R2.1 + R2.8 on real PostgreSQL + RabbitMQ + the HTTP callback surface:
/// a target system that ACCEPTS and answers later, over SIGNED callbacks. Odd customers are
/// accepted. Half of them answer IMMEDIATELY — from inside the handler, before it has even
/// returned Accepted, so the item is still Processing and no acceptance is visible (the
/// race the R2 review's finding 1 named: the old fake provider waited for AwaitingAck and
/// hid it). The other half are answered by a provider loop after the acceptance lands. Two
/// are never answered: the ack sweep turns them into retryable failures and the ladder's
/// second attempt succeeds.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignR2Tests : IAsyncLifetime
{
    /// <summary>The type's callback secret — a test value, read from configuration in a real app.</summary>
    private const string Secret = "integration-only-callback-secret-0123456789";

    /// <summary>Accepted on a first attempt for odd ids; Succeeded otherwise. Ids 1 mod 4 answer before returning.</summary>
    public sealed class AckHandler : IGoldpathCampaignActionHandler<CampaignTests.CustomerTarget>
    {
        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        public static ConcurrentDictionary<int, bool> NeverAnswered { get; } = new();

        /// <summary>The target system answering at once: set by the test, called before Accepted is returned.</summary>
        public static Func<string, CancellationToken, Task<HttpStatusCode>>? AnswerNow { get; set; }

        public static ConcurrentBag<HttpStatusCode> ImmediateStatuses { get; } = [];

        public async Task<GoldpathCampaignActionResult> ExecuteAsync(
            CampaignTests.CustomerTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
            if (context.Attempt != 1 || target.Id % 2 == 0)
            {
                return GoldpathCampaignActionResult.Succeeded();
            }

            var correlation = string.Create(CultureInfo.InvariantCulture, $"corr-{target.Id}-{context.Attempt}");
            if (target.Id % 4 == 1 && !NeverAnswered.ContainsKey(target.Id) && AnswerNow is { } answer)
            {
                // The device answered while our call to the target system was still returning:
                // the callback is fully processed before this handler returns Accepted.
                ImmediateStatuses.Add(await answer(
                    string.Create(CultureInfo.InvariantCulture, $"/goldpath/campaign/callbacks/{context.Type}/{context.CampaignId:N}/{correlation}"),
                    cancellationToken));
            }

            return GoldpathCampaignActionResult.Accepted(correlation, TimeSpan.FromSeconds(4));
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
        AckHandler.ImmediateStatuses.Clear();
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
                .SignedCallbacks(Secret)
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
        _app.MapGoldpathCampaignCallbacks<CampaignTests.CampDb>();   // signed callbacks: no ops credential, no unsigned path
        await _app.StartAsync();
        _client = _app.GetTestClient();
        AckHandler.AnswerNow = async (path, ct) => (await PostSignedAsync(path, new { succeeded = true }, ct)).StatusCode;
    }

    /// <summary>What the target system does: sign the exact path and body it sends.</summary>
    private Task<HttpResponseMessage> PostSignedAsync(string path, object body, CancellationToken ct, string secret = Secret, long? timestamp = null)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body);
        var at = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(json) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(GoldpathCampaignCallbackSignature.TimestampHeader, at.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(GoldpathCampaignCallbackSignature.SignatureHeader, GoldpathCampaignCallbackSignature.Sign(secret, "POST", path, at, json));
        return _client.SendAsync(request, ct);
    }

    public async Task DisposeAsync()
    {
        AckHandler.AnswerNow = null;
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

        // The target system's slow path: answers the acceptances it can see (ids 3 mod 4), except the two scripted silences.
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
                    if (AckHandler.NeverAnswered.ContainsKey(id) || id % 4 == 1)
                    {
                        continue;   // silent, or answered already from inside the handler
                    }

                    var response = await PostSignedAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/{correlation}", new { succeeded = true }, providerStop.Token);
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
        Assert.DoesNotContain(404, statuses);

        // The callbacks that outran their acceptance were PARKED (202), never 404 — and every
        // one of them settled on its first attempt, nothing executed twice.
        Assert.Equal(9, AckHandler.ImmediateStatuses.Count);   // ids 1, 5, 9, … 37 minus the silent 21
        Assert.All(AckHandler.ImmediateStatuses, status => Assert.Equal(HttpStatusCode.Accepted, status));
        Assert.All(items.Where(i => i.Seq % 4 == 1 && i.Seq != 21), i => Assert.Equal(0, i.Attempts));
        Assert.Empty(Query(db => db.Set<GoldpathCampaignParkedCallback>().AsNoTracking().ToList()));

        // The provider retries a webhook it already delivered: found, settled, nothing published.
        var repeat = await PostSignedAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/corr-1-1", new { succeeded = true }, token);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var unknown = await PostSignedAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/corr-nope", new { succeeded = true }, token);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // R2.8: an unsigned callback, one signed by another secret and a stale one are refused before the engine sees them.
        var unsigned = await _client.PostAsJsonAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/corr-3-1", new { succeeded = false }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        var forged = await PostSignedAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/corr-3-1", new { succeeded = false }, token, secret: "a-guessed-secret-of-at-least-32-bytes!!");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        var stale = await PostSignedAsync($"/goldpath/campaign/callbacks/winback/{campaignId:N}/corr-3-1", new { succeeded = false }, token,
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds());
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(40, Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId)).SucceededCount);
    }

    /// <summary>
    /// R2 review #4 on real PostgreSQL: the CampaignR2 migration's data step stamps every R1
    /// waiting retry with the rung it was waiting on, and leaves everything else alone.
    /// </summary>
    [Fact]
    public async Task The_R2_backfill_stamps_R1_waiting_retries_on_PostgreSQL()
    {
        var failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var campaignId = Guid.NewGuid();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            db.Set<GoldpathCampaign>().Add(new GoldpathCampaign
            {
                Id = campaignId,
                Type = "winback",
                Name = "r1",
                State = GoldpathCampaignState.Paused,
                TimeZoneId = "UTC",
                Tps = 1,
                MaxInFlight = 10,
                MaxAttempts = 3,
                CreatedAt = failedAt,
                CreatedBy = "it",
            });
            db.Set<GoldpathCampaignItem>().AddRange(
                new GoldpathCampaignItem { CampaignId = campaignId, Seq = 1, TargetJson = "{}", State = GoldpathCampaignItemState.AwaitingRetry, Attempts = 1, CompletedAt = failedAt },
                new GoldpathCampaignItem { CampaignId = campaignId, Seq = 2, TargetJson = "{}", State = GoldpathCampaignItemState.AwaitingRetry, Attempts = 2, CompletedAt = failedAt },
                new GoldpathCampaignItem { CampaignId = campaignId, Seq = 3, TargetJson = "{}", State = GoldpathCampaignItemState.Failed, Attempts = 3, CompletedAt = failedAt });
            await db.SaveChangesAsync();

            var migration = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder(db.Database.ProviderName);
            migration.GoldpathCampaignR2Backfill();
            var step = (Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation)Assert.Single(migration.Operations);
            Assert.Equal(2, await db.Database.ExecuteSqlRawAsync(step.Sql));
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(step.Sql));   // idempotent
        }

        var items = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == campaignId).OrderBy(i => i.Seq).ToList());
        Assert.InRange((items[0].NextAttemptAt!.Value - failedAt - TimeSpan.FromSeconds(30)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.InRange((items[1].NextAttemptAt!.Value - failedAt - TimeSpan.FromMinutes(2)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Null(items[2].NextAttemptAt);
    }
}
