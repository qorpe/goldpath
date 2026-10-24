// The killable half of the clustered-recovery proofs: a REAL executor process the
// integration test spawns and Process.Kill()s mid-run (in-process hosts can only die
// gracefully — a kill-9 needs a process). Usage:
//   Goldpath.Jobs.TestHost <connectionString> [--trigger]            (jobs cluster mode)
//   Goldpath.Jobs.TestHost <connectionString> --bulk --fleet <name>  (bulk executor mode)
//   Goldpath.Jobs.TestHost <connectionString> --console <url> [--broker <amqp>]
//                                              [--secured] [--multitenant] [--fleet <name>]
//       (console smoke mode: a REAL Goldpath web app serving the FROZEN admin surface for
//        the Playwright gate; with --broker the campaign module joins, since campaign
//        REQUIRES a broker by design (campaign RFC D8) — no in-memory stand-in.
//        --secured raises the AUTH FLOOR (no principal → the surfaces refuse), and
//        --multitenant makes the app tenant-scoped (R1), so the gate can prove how the
//        console behaves when it is refused rather than only when it is welcome.
//        GOLDPATH_CONSOLE_SERVICES / GOLDPATH_CONSOLE_LINKS fill the console's registry;
//        GOLDPATH_LINKS_ONLY_CONSOLE maps a second, links-only console at /linked/console.)
using Goldpath;
using Goldpath.Jobs.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

var connectionString = args[0];
var bulkMode = args.Contains("--bulk");
var consoleMode = args.Contains("--console");
var fleet = args.Contains("--fleet") ? args[Array.IndexOf(args, "--fleet") + 1] : "it-cluster";

if (consoleMode)
{
    await RunConsoleHostAsync(
        connectionString,
        args[Array.IndexOf(args, "--console") + 1],
        args.Contains("--broker") ? args[Array.IndexOf(args, "--broker") + 1] : null,
        args.Contains("--secured"),
        args.Contains("--multitenant"),
        fleet);
    return;
}

var builder = Host.CreateApplicationBuilder();
builder.Configuration["ConnectionStrings:jobsdb"] = connectionString;
builder.Services.AddDbContext<ClusterDb>(o => o.UseNpgsql(connectionString));
if (bulkMode)
{
    builder.Services.AddScoped<IGoldpathBulkRowHandler<ClusterPaymentRow>, SlowPaymentHandler>();
    builder.AddGoldpathBulk<HostApplicationBuilder, ClusterDb>(bulk =>
    {
        bulk.ChunkSize = 5;   // 30 valid rows -> 6 chunks: wide enough for a mid-run kill
        bulk.AddBatch<ClusterPaymentRow>("payments", b => b
            .MaxRows(10_000)
            .RowKey(r => r.EndToEndId));
    });
}

builder.AddGoldpathJobs<HostApplicationBuilder, ClusterDb>(jobs =>
{
    jobs.SchedulerName = fleet;
    jobs.ConnectionName = "jobsdb";
    jobs.CheckinInterval = TimeSpan.FromSeconds(1);
    jobs.CheckinMisfireThreshold = TimeSpan.FromSeconds(3);
    if (bulkMode)
    {
        // Far-future crons: the TEST drives the execute run through the admin verb.
        jobs.AddGoldpathBulkJobs<ClusterDb>(validateCron: "0 0 0 1 1 ? 2099", executeCron: "0 0 0 1 1 ? 2099");
    }
    else
    {
        jobs.AddJob<SlowClusterJob>();
        jobs.AddJob<ChainedProofJob>(j => j.StartAfter<SlowClusterJob>());
    }
});

var host = builder.Build();
await host.StartAsync();
Console.WriteLine("TESTHOST-READY");

if (args.Contains("--trigger"))
{
    var factory = host.Services.GetRequiredService<ISchedulerFactory>();
    var scheduler = await factory.GetScheduler();
    await scheduler.TriggerJob(new JobKey(nameof(SlowClusterJob), GoldpathJobsExtensions.JobGroup));
}

await host.WaitForShutdownAsync();

// The console smoke's service: a REAL Goldpath app (composed from the packages, backed by
// real Postgres + Quartz) whose admin surface the console drives over real HTTP. The ops
// policy is opted OUT explicitly here — the auth floor has its own proofs (H2/A3/A4); this
// host exists to prove the CONSOLE, so it must not need an IdP to do it.
// "name=value;name=value" from an environment variable — how the smoke script hands this
// host its registry (services and links alike) without a config file per run.
static IEnumerable<(string Name, string Value)> Entries(string variable)
{
    foreach (var entry in (Environment.GetEnvironmentVariable(variable) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = entry.Split('=', 2);
        yield return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}

static async Task RunConsoleHostAsync(
    string connectionString, string url, string? brokerUri, bool secured, bool multiTenant, string fleet)
{
    var web = WebApplication.CreateBuilder();
    web.Configuration["ConnectionStrings:jobsdb"] = connectionString;
    web.Services.AddDbContext<ClusterDb>(o => o.UseNpgsql(connectionString));
    // The console origin is NAMED, never reflected: reflected-origin + AllowCredentials
    // is the classic CORS hole (CWE-942), and a test host is still a worked example
    // someone will copy (review R4 on the U2 gate PR).
    var consoleOrigin = Environment.GetEnvironmentVariable("GOLDPATH_CONSOLE_ORIGIN") ?? "http://localhost:5201";
    web.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
        .WithOrigins(consoleOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    if (secured)
    {
        // The auth floor with NO authentication wired (A4): the ops policies exist, so the
        // guarded surfaces answer an honest 401 instead of a 500 — which is exactly the
        // shape the console must render as "forbidden", never as "absent".
        web.AddGoldpathAuth(auth => auth.Strategy = GoldpathAuthStrategy.None);
    }

    if (multiTenant)
    {
        // R1: on a multi-tenant app every admin read is scoped to the ambient tenant, and a
        // request that carries none is REFUSED with a teaching envelope (400).
        web.AddGoldpathMultiTenancy();
    }
    // Bulk is composed too, so the console's capability probe LIGHTS the intake panel and
    // the U3 gate can drive the real four-eyes verbs (upload → validate → approve/reject).
    web.Services.AddScoped<IGoldpathBulkRowHandler<ClusterPaymentRow>, SmokePaymentHandler>();
    web.AddGoldpathBulk<WebApplicationBuilder, ClusterDb>(bulk =>
    {
        bulk.ChunkSize = 5;
        bulk.AddBatch<ClusterPaymentRow>("payments", b => b
            .MaxRows(10_000)
            .RowKey(r => r.EndToEndId));
        // A SECOND definition, so the console smoke can prove ?definition= narrows the
        // QUERY on the server (T8/#72) — one definition could never tell filtered from not.
        bulk.AddBatch<ClusterPaymentRow>("refunds", b => b
            .MaxRows(10_000)
            .RowKey(r => r.EndToEndId));
    });

    // Campaign joins only when a broker exists: the module releases items through the bus,
    // so composing it without one would light a panel over machinery that cannot run.
    if (brokerUri is not null)
    {
        web.Services.AddScoped<IGoldpathCampaignItemHandler<SmokeCustomer>, SmokeCampaignHandler>();
        web.AddGoldpathCampaign<WebApplicationBuilder, ClusterDb>(campaign =>
        {
            campaign.LeadershipSlice = TimeSpan.FromSeconds(5);
            campaign.LeaderTick = TimeSpan.FromMilliseconds(200);
            campaign.EnumerationBatchSize = 100;
            campaign.AddCampaign<SmokeCustomer>("welcome", c => c
                .MaxTargets(1_000)
                // Slow ON PURPOSE: the governor must still be governing something while
                // the operator throttles, pauses, resumes and finally aborts it.
                .DefaultPolicy(policy => policy with { Tps = 2, MaxInFlight = 5 })
                .Targets((services, _) => services.GetRequiredService<ClusterDb>()
                    .CampaignCustomers.AsNoTracking()
                    .OrderBy(x => x.Id)
                    .Select(x => new SmokeCustomer(x.Id, x.Email))
                    .AsAsyncEnumerable()));
        });
        web.Configuration["ConnectionStrings:messaging"] = brokerUri;
        web.AddGoldpathMessaging(messaging => messaging
            .AddGoldpathCampaign<ClusterDb>()
            .UseRabbitMq());
    }

    // FileExchange joins unconditionally: its surface is READ-ONLY, so the gate proves the
    // rail's story — a file the engine ingested at startup with one row applied and one
    // quarantined, in the engine's own words (the T22 console federation proof).
    web.AddGoldpathFileExchange<WebApplicationBuilder, ClusterDb>(files => files
        .AddRail<SmokeRegistryRow>("registry-daily", rail => rail
            .Header(1)
            .ParseLine(line =>
            {
                var parts = line.Split(';');
                return new SmokeRegistryRow(parts[0], decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            })
            .ValidateRow(row => row.Amount > 0 ? null : "non-positive amount")
            .Handle((_, _) => Task.CompletedTask)));

    // Approvals joins too. It was the ONLY console module with no Playwright journey and
    // no axe scan, because this host never composed it (preview.8 coverage audit,
    // 2026-09-05): the panel had unit tests over a mock and nothing that proved the rail,
    // the worklist and the decide verbs against the ENGINE. One real ladder, one real
    // request; the four-eyes refusal below is the engine's, not a fixture's.
    web.AddGoldpathApprovals<WebApplicationBuilder, ClusterDb>(approvals => approvals
        .AddLadder("credit-limit", ladder => ladder
            .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
            .Rung("manager", 5_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)
            .TopRung("general-manager", TimeSpan.FromHours(24))));

    // Erasure REFUSES without DataProtection — classification is what tells the archive
    // which fields to redact (GP1401), so the smoke composes it and marks the holder's
    // name as personal data.
    web.AddGoldpathDataProtection();

    // Archival joins too: the panel's evidence — a chain that verifies, a hold that
    // survives retention, an erasure that redacts without breaking the chain — can only
    // be proven against entries the ENGINE appended.
    web.AddGoldpathArchival<WebApplicationBuilder, ClusterDb>(archival =>
    {
        archival.BatchSize = 50;
        archival.AddArchive<SmokePolicy>(a => a
            .Named("policies")
            .Key(x => x.Id)
            .DueWhen(x => x.ClosedAt != null, x => x.ClosedAt!.Value)
            .ArchiveAfter(TimeSpan.Zero)      // the smoke has no month to wait
            .RetainFor(years: 10));
    });

    // Notification joins unconditionally: its surface is READ-ONLY, so the gate proves
    // the EVIDENCE — a sent row, a suppressed row and a failed row, each written by the
    // module itself. The webhook points back at this host; the email channel points at a
    // dead port ON PURPOSE, because a failure with the transport's own words is evidence
    // the panel must be able to show.
    web.AddGoldpathNotification<WebApplicationBuilder, ClusterDb>(notification =>
    {
        notification.MaxAttempts = 1;
        notification.RetryDelay = TimeSpan.FromMilliseconds(100);
        notification.Webhook(w => w.Url = $"{url.TrimEnd('/')}/smoke/hook");
        notification.Email(e =>
        {
            e.Host = "127.0.0.1";
            e.Port = 9;                       // discard: the connection is refused
            e.From = "noreply@goldpath.local";
            e.UseSsl = false;
            e.AllowInsecureTransport = true;  // A3: plaintext is an explicit opt-in pair
        });
        notification.AddTemplate("welcome", t => t
            .Channel("webhook", c => c.Body("", "Welcome, {{Name}}."))
            .DeleteBodyAfter(TimeSpan.FromDays(90)));
        notification.AddTemplate("ops-alert", t => t
            .Channel("email", c => c
                .Subject("", "Alert")
                .Body("", "Alert: {{Text}}")));
        // Suppression is evidence too: the hook refuses one recipient, and the row that
        // records the refusal is exactly what the panel's Suppressions lens reads.
        notification.MaySend((request, _) => Task.FromResult(!request.Recipient.StartsWith("blocked@", StringComparison.Ordinal)));
    });

    web.AddGoldpathJobs<WebApplicationBuilder, ClusterDb>(jobs =>
    {
        jobs.SchedulerName = fleet;
        jobs.ConnectionName = "jobsdb";
        jobs.CheckinInterval = TimeSpan.FromSeconds(1);
        jobs.AddJob<SmokeJob>();
        // Validation runs on a tight cron so the smoke can WAIT for a real report instead
        // of forging one; execution stays far-future — the gate decides when rows move.
        jobs.AddGoldpathBulkJobs<ClusterDb>(validateCron: "0/5 * * * * ?", executeCron: "0 0 0 1 1 ? 2099");
        jobs.AddGoldpathNotificationJobs<ClusterDb>(sendCron: "0/5 * * * * ?");
        // Archive every 5s so the panel has a real chain within the smoke's lifetime;
        // purge and verify keep their own chained/rare schedules from the module.
        jobs.AddGoldpathArchivalJobs<ClusterDb>(archiveCron: "0/5 * * * * ?");
        if (brokerUri is not null)
        {
            jobs.AddGoldpathCampaignJobs<ClusterDb>(pacerCron: "0/5 * * * * ?");
        }
    });

    var app = web.Build();

    // The smoke owns its database: EnsureCreated provisions BOTH the Goldpath tables and
    // the Quartz schema the model maps, so the script needs no migration step.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ClusterDb>();
        await db.Database.EnsureCreatedAsync();
        if (!await db.Policies.AnyAsync())
        {
            // Closed long ago: the archive job finds them due on its very first fire.
            db.Policies.AddRange(Enumerable.Range(1, 5).Select(i => new SmokePolicy
            {
                PolicyNo = $"P-{i}",
                Holder = $"Holder {i}",
                ClosedAt = DateTimeOffset.UtcNow.AddDays(-400),
            }));
            await db.SaveChangesAsync();
        }

        if (brokerUri is not null && !await db.CampaignCustomers.AnyAsync())
        {
            // A real target population for the pacer to work through.
            db.CampaignCustomers.AddRange(Enumerable.Range(1, 200)
                .Select(i => new SmokeCustomer_Row { Email = $"customer{i}@example.com" }));
            await db.SaveChangesAsync();
        }
    }

    app.Urls.Add(url);
    app.UseCors();
    // The module's OWN primitives, in the order they document: tenant resolution first,
    // so the auth floor sees a resolved ambient tenant (ADR-0003 — compose, never rewrite;
    // hand-copying UseGoldpathAuth's body is how a host drifts from what adopters run).
    if (multiTenant)
    {
        app.UseGoldpathMultiTenancy();
    }

    if (secured)
    {
        app.UseGoldpathAuth();
    }

    var unsecured = !secured;   // secured mode keeps the ops guard ON — that is the point
    app.MapGoldpathJobsAdmin<ClusterDb>(exposeUnsecured: unsecured);
    app.MapGoldpathBulkAdmin<ClusterDb>(exposeUnsecured: unsecured);
    app.MapGoldpathNotificationAdmin<ClusterDb>(exposeUnsecured: unsecured);
    app.MapGoldpathArchivalAdmin<ClusterDb>(exposeUnsecured: unsecured);
    app.MapGoldpathFileExchangeAdmin(exposeUnsecured: unsecured);
    app.MapGoldpathApprovalsAdmin(exposeUnsecured: unsecured);
    // The console SERVED BY THE APP — the shape adopters actually deploy (console RFC D1).
    // Same origin as the admin surfaces, so no CORS is involved in this path at all.
    // The console IS this host's home: whoever lands on the bare port goes there.
    app.MapGet("/", () => Results.Redirect("/goldpath/console/"));

    app.MapGoldpathConsole(exposeUnsecured: unsecured, configure: console =>
    {
        foreach (var (name, adminBaseUrl) in Entries("GOLDPATH_CONSOLE_SERVICES"))
        {
            console.AddService(name, adminBaseUrl);
        }

        // Product console link tiles (#257), the sibling of the services above.
        foreach (var (name, url) in Entries("GOLDPATH_CONSOLE_LINKS"))
        {
            console.AddLink(name, url);
        }
    });

    // A LINKS-ONLY console beside it (#257): the same app, a second prefix, no service at
    // all — the registry shape that must answer `links` with NO `services` key, so the
    // console shows the tiles and no registry warning.
    var linksOnly = Entries("GOLDPATH_LINKS_ONLY_CONSOLE").ToArray();
    if (linksOnly.Length > 0)
    {
        app.MapGoldpathConsole("/linked/console", exposeUnsecured: unsecured, configure: console =>
        {
            foreach (var (name, url) in linksOnly)
            {
                console.AddLink(name, url);
            }
        });
    }

    // A stand-in PRODUCT console a tile can land on (#257) — what Coexist mounts at
    // /coexist/console/ on its own app. Behind the same floor as everything else here:
    // a product surface applies its own floor (platform-SDK D7), and a link grants nothing.
    var product = app.MapGroup("/product");
    AdminSurfaceGuard.Apply(app, product, "/product", unsecured);
    product.MapGet("/console/", () => Results.Content(
        "<!doctype html><html lang=\"en\"><head><title>Product console</title></head><body><main><h1>Product console</h1></main></body></html>",
        "text/html"));
    // The webhook channel's destination: a real endpoint that really answers 200.
    app.MapPost("/smoke/hook", () => Results.Ok());
    if (brokerUri is not null)
    {
        app.MapGoldpathCampaignAdmin<ClusterDb>(exposeUnsecured: unsecured);
    }
    await app.StartAsync();

    // Three real requests: one that sends, one the hook refuses, one whose transport is
    // dead. The send job (cron) does the rest — nothing here forges a state.
    using (var scope = app.Services.CreateScope())
    {
        var notifier = scope.ServiceProvider.GetRequiredService<IGoldpathNotifier>();
        await notifier.RequestAsync(new GoldpathNotificationRequest(
            "welcome", "webhook", "customer1@example.com", "", new Dictionary<string, string> { ["Name"] = "Customer" }, "smoke:welcome:1"), CancellationToken.None);
        await notifier.RequestAsync(new GoldpathNotificationRequest(
            "welcome", "webhook", "blocked@example.com", "", new Dictionary<string, string> { ["Name"] = "Blocked" }, "smoke:welcome:blocked"), CancellationToken.None);
        await notifier.RequestAsync(new GoldpathNotificationRequest(
            "ops-alert", "email", "ops@example.com", "", new Dictionary<string, string> { ["Text"] = "the night is quiet" }, "smoke:alert:1"), CancellationToken.None);
    }

    // One real approval request, routed by AMOUNT: 2.5M lands on the manager rung, whose
    // quorum is two. The panel must show the rung, the quorum and the trail — and the
    // engine must refuse the same person's second signature (four eyes), which is what the
    // journey drives.
    using (var scope = app.Services.CreateScope())
    {
        var approvals = scope.ServiceProvider.GetRequiredService<GoldpathApprovalEngine>();
        await approvals.RequestAsync("credit-limit", "Limit raise for ACME", 2_500_000m, "analyst@example.com", CancellationToken.None);
    }

    // One real file through the rail: two rows apply, one quarantines with its reason —
    // the panel's story is written by the ENGINE, nothing here forges a ledger row.
    using (var scope = app.Services.CreateScope())
    {
        var rails = scope.ServiceProvider.GetRequiredService<GoldpathFileRailEngine>();
        await rails.ProcessAsync("registry-daily", "registry-2026-09-03.csv", ["reference;amount", "R-1;10.00", "R-2;0", "R-3;5.50"], CancellationToken.None);
    }

    Console.WriteLine("CONSOLEHOST-READY");
    await app.WaitForShutdownAsync();
}

namespace Goldpath.Jobs.TestHost
{
    /// <summary>Shared model: run + bulk tables + cross-process execution sinks.</summary>
    public class ClusterDb(DbContextOptions<ClusterDb> options) : DbContext(options)
    {
        public DbSet<SinkEntry> Sink => Set<SinkEntry>();

        public DbSet<PaymentSinkEntry> PaymentSink => Set<PaymentSinkEntry>();

        public DbSet<SmokeCustomer_Row> CampaignCustomers => Set<SmokeCustomer_Row>();

        public DbSet<SmokePolicy> Policies => Set<SmokePolicy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddGoldpathJobs();
            modelBuilder.AddGoldpathBulk();
            modelBuilder.AddGoldpathCampaign();
            modelBuilder.AddGoldpathNotification();
            modelBuilder.AddGoldpathArchiveModel();
            modelBuilder.AddGoldpathFileExchangeModel();
            modelBuilder.AddGoldpathApprovalModel();
        }
    }

    /// <summary>One line of the smoke's registry file.</summary>
    public sealed record SmokeRegistryRow(string Reference, decimal Amount);

    /// <summary>The campaign's target population (console smoke).</summary>
    public class SmokeCustomer_Row
    {
        public int Id { get; set; }

        public string Email { get; set; } = "";
    }

    /// <summary>The archivable aggregate of the console smoke: a closed policy.</summary>
    public class SmokePolicy
    {
        public int Id { get; set; }

        public string PolicyNo { get; set; } = "";

        [GoldpathPersonalData]
        public string Holder { get; set; } = "";

        public DateTimeOffset? ClosedAt { get; set; }
    }

    /// <summary>One target as the campaign type projects it.</summary>
    public sealed record SmokeCustomer(int Id, string Email);

    /// <summary>Sends the "welcome" — records the side effect, nothing else.</summary>
    public sealed class SmokeCampaignHandler(ClusterDb db) : IGoldpathCampaignItemHandler<SmokeCustomer>
    {
        public async Task ExecuteAsync(SmokeCustomer target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
        {
            db.Sink.Add(new SinkEntry { JobName = "welcome", ChunkIndex = target.Id, Instance = "campaign" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>One chunk execution, recorded durably so the test sees across processes.</summary>
    public class SinkEntry
    {
        public long Id { get; set; }
        public string JobName { get; set; } = "";
        public int ChunkIndex { get; set; }
        public string Instance { get; set; } = "";
    }

    /// <summary>One paid row — the double-payment detector across processes.</summary>
    public class PaymentSinkEntry
    {
        public long Id { get; set; }
        public string EndToEndId { get; set; } = "";
        public int RowNumber { get; set; }
        public string Instance { get; set; } = "";
    }

    /// <summary>The payment instruction shape of the bulk kill-9 proof.</summary>
    public sealed class ClusterPaymentRow
    {
        public string EndToEndId { get; set; } = "";
        public decimal Amount { get; set; }
    }

    /// <summary>Pays one row SLOWLY (so a kill lands mid-chunk) and records the side effect.</summary>
    public sealed class SlowPaymentHandler(ClusterDb db) : IGoldpathBulkRowHandler<ClusterPaymentRow>
    {
        public async Task ExecuteAsync(ClusterPaymentRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken)
        {
            db.PaymentSink.Add(new PaymentSinkEntry
            {
                EndToEndId = row.EndToEndId,
                RowNumber = context.RowNumber,
                Instance = Environment.ProcessId.ToString(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);   // wide kill window
        }
    }

    /// <summary>30 slow chunks — wide enough for a mid-run kill to land.</summary>
    public sealed class SlowClusterJob : IGoldpathJob
    {
        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
            => Task.FromResult(GoldpathJobPlanner.ByRange(30, 1));

        public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
        {
            var db = context.Services.GetRequiredService<ClusterDb>();
            db.Sink.Add(new SinkEntry { JobName = nameof(SlowClusterJob), ChunkIndex = chunk.Index, Instance = context.InstanceName });
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    /// <summary>
    /// The console smoke's job: four chunks, one of which reports an item failure — so a
    /// single trigger produces a run the console can watch complete AND a repair queue it
    /// can replay. Nothing is faked: the engine writes the rows the console reads.
    /// </summary>
    public sealed class SmokeJob : IGoldpathJob
    {
        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
            => Task.FromResult(GoldpathJobPlanner.ByRange(4, 1));

        public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
        {
            if (chunk.Index == 2)
            {
                chunk.ReportItemFailure($"ORD-{chunk.Index}7", "the bank refused this instruction");
            }

            var db = context.Services.GetRequiredService<ClusterDb>();
            db.Sink.Add(new SinkEntry { JobName = nameof(SmokeJob), ChunkIndex = chunk.Index, Instance = context.InstanceName });
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
    }

    /// <summary>The console smoke's row handler: records the payment, no artificial delay.</summary>
    public sealed class SmokePaymentHandler(ClusterDb db) : IGoldpathBulkRowHandler<ClusterPaymentRow>
    {
        public async Task ExecuteAsync(ClusterPaymentRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken)
        {
            db.PaymentSink.Add(new PaymentSinkEntry
            {
                EndToEndId = row.EndToEndId,
                RowNumber = context.RowNumber,
                Instance = Environment.ProcessId.ToString(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Chaining proof: must run exactly once, AFTER the slow job completes.</summary>
    public sealed class ChainedProofJob : IGoldpathJob
    {
        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
            => Task.FromResult(GoldpathJobPlanner.ByRange(1, 1));

        public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
        {
            var db = context.Services.GetRequiredService<ClusterDb>();
            db.Sink.Add(new SinkEntry { JobName = nameof(ChainedProofJob), ChunkIndex = chunk.Index, Instance = context.InstanceName });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
