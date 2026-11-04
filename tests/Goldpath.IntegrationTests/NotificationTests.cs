using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The insurance renewal story on real PostgreSQL with the REAL runner AND a REAL SMTP
/// server (smtp4dev): request (rendered, hash-stamped, dedup-guarded) → the send run
/// claims and the mail ACTUALLY LANDS → evidence stamped; a poisoned webhook exhausts its
/// attempts into the repair queue and the admin replay-items verb re-sends it once the
/// world is fixed. Claim-before-send proven against the wire. The facts are provider-neutral:
/// PostgreSQL runs them (<see cref="NotificationTests"/>), and Oracle runs them in the Oracle lane
/// (<see cref="NotificationOracleTests"/>, G7 O-5b, #410), the Quartz store included.
/// </summary>
public abstract class NotificationFacts : IAsyncLifetime
{
    public sealed class NotifyDb(DbContextOptions<NotifyDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddGoldpathNotification();
            modelBuilder.AddGoldpathJobs();
        }
    }

    private readonly IContainer _smtp = new ContainerBuilder("rnwood/smtp4dev:3.8.6")
        .WithPortBinding(25, assignRandomHostPort: true)
        .WithPortBinding(80, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/api/version")))
        .Build();

    private IHost _host = null!;
    private readonly string _fleet = $"notify-{Guid.NewGuid():N}"[..16];   // unique per test: Quartz's SchedulerRepository is process-global

    /// <summary>Starts an empty database and returns its connection string.</summary>
    protected abstract Task<string> StartDatabaseAsync();

    /// <summary>Releases what <see cref="StartDatabaseAsync"/> started.</summary>
    protected abstract Task StopDatabaseAsync();

    /// <summary>Points a context at the database.</summary>
    protected abstract void Use(DbContextOptionsBuilder options, string connection);

    /// <summary>The Quartz store's provider for this database.</summary>
    protected abstract GoldpathJobStoreProvider JobStore { get; }

    /// <summary>
    /// The open issue under which this provider refuses a notification rendered from the invariant fallback (its
    /// culture is the empty name), or <see langword="null"/> where it is stored. While it is set, the fact that
    /// requests one asserts that refusal and stops: the day the issue lands, the assertion goes red.
    /// </summary>
    protected virtual string? InvariantCultureRefusedUnder => null;

    public async Task InitializeAsync()
    {
        var connection = await StartDatabaseAsync();
        await _smtp.StartAsync();
        var options = new DbContextOptionsBuilder<NotifyDb>();
        Use(options, connection);
        await using (var db = new NotifyDb(options.Options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:notifydb"] = connection;
        builder.Services.AddDbContext<NotifyDb>(o => Use(o, connection));
        builder.AddGoldpathNotification<HostApplicationBuilder, NotifyDb>(notification =>
        {
            notification.RetryDelay = TimeSpan.FromMilliseconds(50);
            notification.Email(e =>
            {
                e.Host = _smtp.Hostname;
                e.Port = _smtp.GetMappedPublicPort(25);
                e.From = "noreply@goldpath.local";
                // The dev-relay container speaks plaintext — the A3 contract makes that
                // an EXPLICIT opt-in pair, exactly what an adopter writes for MailHog.
                e.UseSsl = false;
                e.AllowInsecureTransport = true;
            });
            notification.Webhook(w => w.Url = "http://127.0.0.1:9/refuses");   // port 9: the poisoned hook
            notification.AddTemplate("policy-renewal", t => t
                .Channel("email", c => c
                    .Subject("tr", "Poliçeniz {{PolicyNo}} yenilenmek üzere")
                    .Body("tr", "Sayın {{Name}}, poliçeniz {{RenewalDate}} tarihinde yenilenecektir.")
                    .Subject("", "Your policy {{PolicyNo}} is up for renewal")
                    .Body("", "Dear {{Name}}, your policy renews on {{RenewalDate}}."))
                .DeleteBodyAfter(TimeSpan.FromDays(90)));
            notification.AddTemplate("ops-alert", t => t
                .Channel("webhook", c => c.Body("", "Alert: {{Text}}")));
        });
        builder.AddGoldpathJobs<HostApplicationBuilder, NotifyDb>(jobs =>
        {
            jobs.ConnectionName = "notifydb";
            jobs.SchedulerName = _fleet;
            jobs.Provider = JobStore;
            // Far-future crons: the test drives every run through the ADMIN verb.
            jobs.AddGoldpathNotificationJobs<NotifyDb>(sendCron: "0 0 0 1 1 ? 2099", retentionCron: "0 0 0 1 1 ? 2099");
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();
        await Task.WhenAll(StopDatabaseAsync(), _smtp.DisposeAsync().AsTask());
    }

    private GoldpathJobsAdminService<NotifyDb> Admin => _host.Services.GetRequiredService<GoldpathJobsAdminService<NotifyDb>>();

    private T Query<T>(Func<NotifyDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<NotifyDb>());
    }

    private async Task WaitForStateAsync(Guid id, GoldpathNotificationState target, CancellationToken token)
    {
        while (Query(db => db.Set<GoldpathNotification>().AsNoTracking().Single(n => n.Id == id).State) != target)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private async Task WaitForFleetAsync(CancellationToken token)
    {
        while (true)
        {
            try
            {
                if ((await Admin.GetJobsAsync(_fleet, token)).Count >= 2)
                {
                    return;
                }
            }
            catch (Quartz.JobPersistenceException)
            {
                // Connection warm-up on a container that JUST reported ready — transient,
                // bounded by the test's own timeout token.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    [Fact]
    public async Task The_renewal_mail_actually_lands_and_the_evidence_stamps()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        Guid id;
        using (var scope = _host.Services.CreateScope())
        {
            var notifier = scope.ServiceProvider.GetRequiredService<IGoldpathNotifier>();
            var notification = await notifier.RequestAsync(new GoldpathNotificationRequest(
                "policy-renewal", "email", "omer@example.test", "tr",
                new Dictionary<string, string> { ["Name"] = "Ömer", ["PolicyNo"] = "P-42", ["RenewalDate"] = "2026-08-01" },
                dedupKey: "renewal:P-42:2026-08"), timeout.Token);
            id = notification.Id;

            // Dedup on real pg (the unique index, not luck).
            var again = await notifier.RequestAsync(new GoldpathNotificationRequest(
                "policy-renewal", "email", "omer@example.test", "tr",
                new Dictionary<string, string> { ["Name"] = "Ömer", ["PolicyNo"] = "P-42", ["RenewalDate"] = "2026-08-01" },
                dedupKey: "renewal:P-42:2026-08"), timeout.Token);
            Assert.Equal(id, again.Id);
        }

        await WaitForFleetAsync(timeout.Token);
        Assert.True((await Admin.TriggerAsync(_fleet, "GoldpathNotificationSendJob`1", dryRun: false, "it-operator", timeout.Token)).Ok);
        await WaitForStateAsync(id, GoldpathNotificationState.Sent, timeout.Token);

        var row = Query(db => db.Set<GoldpathNotification>().AsNoTracking().Single(n => n.Id == id));
        Assert.True(row.ClaimedAt <= row.SentAt, "the claim persists BEFORE the wire call");
        Assert.NotEmpty(row.TemplateHash);

        // The mail REALLY landed: ask the SMTP server.
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_smtp.Hostname}:{_smtp.GetMappedPublicPort(80)}") };
        var inbox = await http.GetFromJsonAsync<Smtp4DevPage>("/api/messages", timeout.Token);
        var mail = Assert.Single(inbox!.Results);
        Assert.Equal("Poliçeniz P-42 yenilenmek üzere", mail.Subject);
        Assert.Contains("omer@example.test", mail.To[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dead_webhook_exhausts_into_the_repair_queue_and_replay_resends_after_the_fix()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        Guid id;
        using (var scope = _host.Services.CreateScope())
        {
            var request = new GoldpathNotificationRequest("ops-alert", "webhook", "ops-room", "",
                new Dictionary<string, string> { ["Text"] = "disk full" }, dedupKey: "alert:disk:2026-07-08");
            var notifier = scope.ServiceProvider.GetRequiredService<IGoldpathNotifier>();
            if (InvariantCultureRefusedUnder is not null)
            {
                // The template exists only in the invariant culture, so the stored culture is "" — NULL on this provider.
                var refused = await Assert.ThrowsAsync<DbUpdateException>(() => notifier.RequestAsync(request, timeout.Token));
                Assert.Contains("ORA-01400", refused.InnerException?.Message, StringComparison.Ordinal);
                return;
            }

            id = (await notifier.RequestAsync(request, timeout.Token)).Id;
        }

        await WaitForFleetAsync(timeout.Token);
        Assert.True((await Admin.TriggerAsync(_fleet, "GoldpathNotificationSendJob`1", dryRun: false, "it-operator", timeout.Token)).Ok);
        await WaitForStateAsync(id, GoldpathNotificationState.Failed, timeout.Token);

        var failed = Query(db => db.Set<GoldpathNotification>().AsNoTracking().Single(n => n.Id == id));
        Assert.Equal(3, failed.Attempts);
        // The repair row commits with the CHUNK's checkpoint, which can land after the
        // notification's own Failed flip — poll it, never race it (issue #39).
        GoldpathJobItemFailure? failure;
        while ((failure = Query(db => db.Set<GoldpathJobItemFailure>().AsNoTracking().FirstOrDefault(f => f.ItemKey == id.ToString("N")))) is null)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }

        var runId = failure.RunId;

        // Fix the world: a live hook that ACCEPTS the POST (a tiny in-test listener).
        using var listener = new System.Net.HttpListener();
        var port = FreeTcpPort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/hook/");
        listener.Start();
        var received = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            var http = await listener.GetContextAsync();
            using var reader = new StreamReader(http.Request.InputStream);
            received.TrySetResult(await reader.ReadToEndAsync());
            http.Response.StatusCode = 200;
            http.Response.Close();
        }, timeout.Token);
        _host.Services.GetRequiredService<GoldpathNotificationOptions>().WebhookOptions.Url = $"http://127.0.0.1:{port}/hook/";

        Assert.True((await Admin.ReplayItemsAsync(runId, "it-operator", timeout.Token)).Ok);
        await WaitForStateAsync(id, GoldpathNotificationState.Sent, timeout.Token);   // the TRANSITION, not the stale state
        Assert.Contains("disk full", await received.Task.WaitAsync(timeout.Token), StringComparison.Ordinal);   // the hook really got it
    }

    private static int FreeTcpPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed record Smtp4DevPage(List<Smtp4DevMessage> Results);

    private sealed record Smtp4DevMessage(string Subject, List<string> To);
}

/// <summary><see cref="NotificationFacts"/> on PostgreSQL: a container per test.</summary>
[Collection("quartz-process-globals")]
public sealed class NotificationTests : NotificationFacts
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override GoldpathJobStoreProvider JobStore => GoldpathJobStoreProvider.Postgres;

    protected override async Task<string> StartDatabaseAsync()
    {
        await _postgres.StartAsync();
        return _postgres.GetConnectionString();
    }

    protected override Task StopDatabaseAsync() => _postgres.DisposeAsync().AsTask();

    protected override void Use(DbContextOptionsBuilder options, string connection) => options.UseNpgsql(connection);
}
