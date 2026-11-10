using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
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
/// Campaign revision R3.4 on real PostgreSQL + RabbitMQ + the signed callback surface (slice R3-S6, #316): a
/// simulated target system that ACCEPTS every request and calls back eight seconds later. With
/// <c>MaxInFlight = 4</c> and <c>MaxAwaitingAck = 40</c>, live calls (<c>Released + Processing + AwaitingRetry</c>,
/// sampled from the rows throughout) never pass four while the outstanding acknowledgements climb far above it and
/// never pass forty, and every item completes exactly once. A campaign without the field on the same host is the
/// contrast: there an acceptance keeps its live slot, so the acknowledgements cannot pass four either.
/// <c>R3AwaitingAckTests</c> proves the pacer rule on SQLite and a stepped fleet; this proves it where the broker,
/// the sink's batching and the callbacks really race.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class CampaignAwaitingAckProof : IAsyncLifetime
{
    private const string Secret = "integration-only-callback-secret-awaiting-ack";
    private const int MaxInFlight = 4;
    private const int MaxAwaitingAck = 40;
    private static readonly TimeSpan CallbackDelay = TimeSpan.FromSeconds(8);

    /// <summary>The target system: accepts every call after a short latency and promises to answer later.</summary>
    public sealed class SlowAckHandler : IGoldpathCampaignActionHandler<CampaignTests.CustomerTarget>
    {
        public static ConcurrentDictionary<int, int> Executions { get; } = new();

        /// <summary>Callbacks the target system owes: the path to answer on and when it answers.</summary>
        public static ConcurrentQueue<(string Path, DateTimeOffset Due)> Owed { get; } = new();

        private static int _live;

        /// <summary>The most calls the target system ever had open at once, measured inside the handler.</summary>
        public static int MaxConcurrent;

        public async Task<GoldpathCampaignActionResult> ExecuteAsync(
            CampaignTests.CustomerTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _live);
            int seen;
            while (now > (seen = Volatile.Read(ref MaxConcurrent)) && Interlocked.CompareExchange(ref MaxConcurrent, now, seen) != seen)
            {
            }

            try
            {
                Executions.AddOrUpdate(target.Id, 1, (_, count) => count + 1);
                await Task.Delay(50, cancellationToken);   // the call into the target system
                var correlation = string.Create(CultureInfo.InvariantCulture, $"slow-{target.Id}-{context.Attempt}");
                Owed.Enqueue((
                    string.Create(CultureInfo.InvariantCulture, $"/goldpath/campaign/callbacks/{context.Type}/{context.CampaignId:N}/{correlation}"),
                    DateTimeOffset.UtcNow + CallbackDelay));
                return GoldpathCampaignActionResult.Accepted(correlation, TimeSpan.FromMinutes(5));
            }
            finally
            {
                Interlocked.Decrement(ref _live);
            }
        }

        public static void Reset()
        {
            Executions.Clear();
            Owed.Clear();
            _live = 0;
            MaxConcurrent = 0;
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private readonly string _fleet = $"r3a-{Guid.NewGuid():N}"[..16];   // Quartz's SchedulerRepository is process-global
    private readonly ITestOutputHelper _output;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public CampaignAwaitingAckProof(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        SlowAckHandler.Reset();
        await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());
        await using (var db = new CampaignTests.CampDb(new DbContextOptionsBuilder<CampaignTests.CampDb>().UseNpgsql(_postgres.GetConnectionString()).Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);   // the SQL log of a minute's sampling would drown the result lines
        builder.Configuration["ConnectionStrings:campdb"] = _postgres.GetConnectionString();
        builder.Services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddScoped<IGoldpathCampaignActionHandler<CampaignTests.CustomerTarget>, SlowAckHandler>();
        builder.AddGoldpathCampaign<WebApplicationBuilder, CampaignTests.CampDb>(campaign =>
        {
            // One slice outlasts the whole run, so the slice-start re-count of AwaitingAckCount happens once, before
            // anything is in flight. A re-count landing between a flush's item updates and its counter update may
            // leave the live split off by that flush's rows until the next slice (README, "What is exact and what
            // is not"); that documented window is not what this test measures, and leaving it in would make the
            // strict bound below flaky rather than wrong.
            campaign.LeadershipSlice = TimeSpan.FromMinutes(3);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(100);
            campaign.EnumerationBatchSize = 500;
            campaign.AddCampaign<CampaignTests.CustomerTarget>("winback", c => c
                .MaxTargets(10_000)
                .SignedCallbacks(Secret)
                .Targets((services, parameters) => services.GetRequiredService<CampaignTests.CampDb>()
                    .Customers.AsNoTracking()
                    .Where(x => x.Email.StartsWith(parameters["cohort"]))
                    .OrderBy(x => x.Id)
                    .Select(x => new CampaignTests.CustomerTarget(x.Id, x.Email))
                    .AsAsyncEnumerable()));
        });
        builder.Services.WaitForTheBusOnStart();
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddGoldpathCampaign<CampaignTests.CampDb>();
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        });
        builder.AddGoldpathJobs<WebApplicationBuilder, CampaignTests.CampDb>(jobs =>
        {
            jobs.ConnectionName = "campdb";
            jobs.SchedulerName = _fleet;
            jobs.AddGoldpathCampaignJobs<CampaignTests.CampDb>(pacerCron: "0/5 * * * * ?");
        });
        _app = builder.Build();
        _app.MapGoldpathCampaignCallbacks<CampaignTests.CampDb>();
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

    private Task<HttpResponseMessage> PostSignedAsync(string path, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { succeeded = true });
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(json) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(GoldpathCampaignCallbackSignature.TimestampHeader, at.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(GoldpathCampaignCallbackSignature.SignatureHeader, GoldpathCampaignCallbackSignature.Sign(Secret, "POST", path, at, json));
        return _client.SendAsync(request, ct);
    }

    /// <summary>
    /// One consistent read (a single statement) of what the campaign has out: live calls by the issue's definition —
    /// <c>Released + Processing + AwaitingRetry</c>, plus a published item the pacer counted whose Released stamp has
    /// not landed yet — and the acceptances outstanding.
    /// </summary>
    private (long Live, long AwaitingAck) Sample(Guid campaignId) => Query(db =>
    {
        var releasedThrough = db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId).Select(c => c.ReleasedThrough);
        var counts = db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaignId
                && (i.State == GoldpathCampaignItemState.Released
                    || i.State == GoldpathCampaignItemState.Processing
                    || i.State == GoldpathCampaignItemState.AwaitingRetry
                    || i.State == GoldpathCampaignItemState.AwaitingAck
                    || (i.State == GoldpathCampaignItemState.Pending && i.Seq <= releasedThrough.First())))
            .GroupBy(i => i.State == GoldpathCampaignItemState.AwaitingAck)
            .Select(g => new { Ack = g.Key, Count = g.LongCount() })
            .ToList();
        return (counts.Where(c => !c.Ack).Sum(c => c.Count), counts.Where(c => c.Ack).Sum(c => c.Count));
    });

    private async Task<(long MaxLive, long MaxAwaitingAck, int Samples, GoldpathCampaign Done, int Callbacks)> RunAsync(
        string cohort, int targets, int? maxAwaitingAck, TimeSpan budget, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        Guid campaignId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
            var first = await db.Customers.CountAsync(token) + 1;
            db.Customers.AddRange(Enumerable.Range(first, targets).Select(i => new CampaignTests.Customer
            {
                Id = i,
                Email = string.Create(CultureInfo.InvariantCulture, $"{cohort}{i}@example.test"),
                LastOrderAt = DateTimeOffset.UtcNow.AddYears(-1),
            }));
            await db.SaveChangesAsync(token);
            var campaign = await scope.ServiceProvider.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>()
                .CreateAsync(scope.ServiceProvider, "winback", cohort, new Dictionary<string, string> { ["cohort"] = cohort },
                    new GoldpathCampaignPolicy(1_000, null, MaxInFlight, null, null, "UTC") { MaxAwaitingAck = maxAwaitingAck },
                    tenant: null, actor: "it-operator", token);
            campaignId = campaign.Id;
        }

        // The target system's answering loop: every owed callback, once it is due. 202 (published, or parked when it
        // outran its acceptance) is the only answer a first callback may get.
        var statuses = new ConcurrentBag<HttpStatusCode>();
        using var providerStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var provider = Task.Run(async () =>
        {
            var notYet = new List<(string Path, DateTimeOffset Due)>();
            while (!providerStop.IsCancellationRequested)
            {
                while (SlowAckHandler.Owed.TryDequeue(out var owed))
                {
                    notYet.Add(owed);
                }

                foreach (var due in notYet.Where(o => o.Due <= DateTimeOffset.UtcNow).ToList())
                {
                    statuses.Add((await PostSignedAsync(due.Path, providerStop.Token)).StatusCode);
                    notYet.Remove(due);
                }

                await Task.Delay(100, providerStop.Token);
            }
        }, providerStop.Token);

        long maxLive = 0, maxAcks = 0;
        var samples = 0;
        GoldpathCampaign done;
        while ((done = Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single(c => c.Id == campaignId))).State
               is not (GoldpathCampaignState.Completed or GoldpathCampaignState.CompletedWithFailures))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                break;   // the caller asserts the ceilings first, then completion, so a stall fails on what it measured
            }

            var (live, acks) = Sample(campaignId);
            samples++;
            maxLive = Math.Max(maxLive, live);
            maxAcks = Math.Max(maxAcks, acks);
            await Task.Delay(25, CancellationToken.None);
        }

        providerStop.Cancel();
        try
        {
            await provider;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Accepted, s));
        return (maxLive, maxAcks, samples, done, statuses.Count);
    }

    [Fact]
    public async Task Live_calls_stay_under_MaxInFlight_while_ten_times_as_many_acknowledgements_are_outstanding()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        // The contrast first: no MaxAwaitingAck, R2's rule — an acceptance keeps its live slot.
        var r2 = await RunAsync("r2-", targets: 8, maxAwaitingAck: null, TimeSpan.FromSeconds(90), timeout.Token);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"R2 rule: max live {r2.MaxLive}, max awaiting ack {r2.MaxAwaitingAck}, over {r2.Samples} samples"));
        Assert.InRange(r2.MaxAwaitingAck, 0, MaxInFlight);
        Assert.Equal((GoldpathCampaignState.Completed, 8L), (r2.Done.State, r2.Done.SucceededCount));

        SlowAckHandler.Reset();
        const int targets = 120;
        var r3 = await RunAsync("r3-", targets, MaxAwaitingAck, TimeSpan.FromSeconds(150), timeout.Token);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"R3.4: max live {r3.MaxLive} (MaxInFlight {MaxInFlight}), max awaiting ack {r3.MaxAwaitingAck} (MaxAwaitingAck {MaxAwaitingAck}), handler concurrency {SlowAckHandler.MaxConcurrent}, over {r3.Samples} samples"));

        Assert.True(r3.Samples >= 50, string.Create(CultureInfo.InvariantCulture, $"only {r3.Samples} samples: the run was not watched throughout"));
        // Live calls never pass MaxInFlight — in the rows, and inside the target system itself.
        Assert.InRange(r3.MaxLive, 1, MaxInFlight);
        Assert.InRange(SlowAckHandler.MaxConcurrent, 1, MaxInFlight);
        // The acknowledgements reach well above MaxInFlight (five times it at least), and never pass their own ceiling.
        Assert.InRange(r3.MaxAwaitingAck, 5 * MaxInFlight, MaxAwaitingAck);

        // Every item completed exactly once: one execution each, one success each, nothing failed or left counted.
        // About 35 s with the ceiling working; R2's rule (an acceptance holding its live slot) needs about 240 s and
        // has already failed above on the acknowledgement count.
        Assert.Equal(GoldpathCampaignState.Completed, r3.Done.State);
        Assert.Equal(targets, r3.Done.SucceededCount);
        Assert.Equal(0, r3.Done.FailedCount);
        Assert.Equal(0, r3.Done.AwaitingAckCount);
        Assert.Equal(targets, r3.Callbacks);   // one answer per item, each a 202
        Assert.Equal(targets, SlowAckHandler.Executions.Count);
        Assert.All(SlowAckHandler.Executions, e => Assert.Equal(1, e.Value));
        var items = Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Where(i => i.CampaignId == r3.Done.Id).ToList());
        Assert.Equal(targets, items.Count);
        Assert.All(items, i => Assert.Equal((GoldpathCampaignItemState.Succeeded, 0), (i.State, i.Attempts)));
    }
}
