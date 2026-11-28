using System.Text;

namespace Goldpath.Cli;

/// <summary>
/// <c>goldpath add worker NAME --trigger queue|schedule|jobs</c> — adds a worker PROJECT to an
/// existing solution: the generated skeleton (exactly what <c>goldpath new worker</c> ships,
/// renamed into the solution), the sln entry, the AppHost project reference and the
/// AddProject wiring with the right resource chain. The pieces that were "manual" close:
/// the finance-sample shape (API + consumer + EOD worker in ONE solution) is one verb away.
/// Ends with the same specdrift round-trip as <c>add feature</c>; findings restore everything.
/// </summary>
public static class AddWorkerCommand
{
    private static readonly string[] Triggers = ["queue", "schedule", "jobs"];

    /// <summary>Adds the worker project, verifies with the engine, rolls back on findings.</summary>
    public static int Run(string name, string trigger, string appRoot, IProcessRunner runner, TextWriter output, TextWriter error)
    {
        if (!Triggers.Contains(trigger))
        {
            throw new CliUsageException($"unknown trigger '{trigger}' — one of: {string.Join(", ", Triggers)}");
        }

        var manifestPath = Path.Combine(appRoot, ".goldpath", "manifest.yaml");
        if (!File.Exists(manifestPath))
        {
            throw new CliFailureException($"no manifest at {manifestPath} — goldpath add runs inside a Goldpath-generated app (or pass --path).");
        }

        if (ManifestEditor.ReadKind(File.ReadAllText(manifestPath)) is var kind && kind != "solution")
        {
            throw new CliFailureException(
                $"this manifest is kind '{kind ?? "<none>"}' — workers join a SOLUTION's AppHost; run goldpath add there.");
        }

        var files = AppFiles.Locate(appRoot);
        var facts = AppFacts.Read(files);
        facts.RequireWritableProvider("add worker");
        if (trigger == "queue" && !facts.MessagingWired)
        {
            throw new CliFailureException(
                "a queue worker consumes from the broker — no AddGoldpathMessaging(...) found in the composition root. Wire messaging first (a broker resource + the AddGoldpathMessaging block), then re-run.");
        }

        if (trigger is "queue" or "jobs" && facts.ConnectionName is null)
        {
            throw new CliFailureException(
                "no GetConnectionString(...) found in the composition root — this worker persists into the app database and needs its connection name.");
        }

        if (trigger == "jobs" && !facts.JobsWired)
        {
            // The in-solution fleet SHARES the app database's jobs tables, which the Api's
            // context owns (migrations RFC D3: the worker maps them excludeFromMigrations).
            // Without a rider on the Api there are no tables to share — the fleet would boot
            // against nothing (found by the GmWorkerInSolution shape, 2026-09-05), and the
            // manifest would wire no Goldpath.Jobs (the T26 warning). Refuse in words.
            throw new CliFailureException(
                "a jobs worker shares the app database's jobs tables, which the Api's context owns — no AddGoldpathJobs(...) found in the composition root. Enable a jobs rider first (goldpath add feature archival|bulk|notification|campaign|approvals|fileexchange), or generate a STANDALONE worker with its own store (goldpath new worker --trigger jobs).");
        }

        var prefix = ApiPrefix(files.ApiProject);
        var pascal = Pascal(name);
        var projectName = $"{prefix}.{pascal}Worker";
        var kebab = $"{Kebab(pascal)}-worker";
        var projectDir = Path.Combine(appRoot, "src", projectName);
        if (Directory.Exists(projectDir))
        {
            throw new CliFailureException($"src/{projectName} already exists — pick another name or remove it first.");
        }

        var solutionFile = Directory.EnumerateFiles(appRoot, "*.sln", SearchOption.TopDirectoryOnly).ToList() switch
        {
            [var single] => single,
            [] => throw new CliFailureException("no .sln at the app root — goldpath add worker wires the project into the solution."),
            var many => throw new CliFailureException($"{many.Count} .sln files at the app root — goldpath cannot choose; keep exactly one."),
        };

        // Snapshot BEFORE touching anything: red engine = the app comes back byte-identical.
        var touched = new[] { files.AppHostFile, files.AppHostProject, solutionFile };
        var snapshot = touched.ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        try
        {
            WriteProject(projectDir, projectName, trigger, facts);
            WireAppHost(files, projectName, kebab, trigger, facts);

            var slnExit = runner.Run("dotnet", ["sln", solutionFile, "add", Path.Combine(projectDir, $"{projectName}.csproj")], appRoot);
            if (slnExit != 0)
            {
                throw new CliFailureException("dotnet sln add failed — see its output above.");
            }

            output.WriteLine($"goldpath: worker '{projectName}' ({trigger}) wired — running the engine (specdrift validate + drift)");
            var exitCode = SpecdriftGate.Validate(appRoot, runner);
            if (exitCode == 0)
            {
                exitCode = SpecdriftGate.Drift(appRoot, runner);
            }

            if (exitCode != 0)
            {
                Restore(snapshot, projectDir);
                error.WriteLine($"goldpath: the engine rejected the result — ALL files restored; fix the findings above and retry (the worker was NOT added).");
                return 1;
            }
        }
        catch
        {
            Restore(snapshot, projectDir);
            throw;
        }

        output.WriteLine($"goldpath: worker '{projectName}' added as resource '{kebab}' — engine clean. Your decisions (goldpath never guesses domain opt-ins):");
        foreach (var step in NextSteps(trigger))
        {
            output.WriteLine($"  → {step}");
        }

        if (facts.AuthWired)
        {
            output.WriteLine("  → the worker's head is behind the SAME floor as the Api: copy the Goldpath:Auth section (Authority + Audience, or ApiKeys) into this project's appsettings — Aspire does not share configuration across projects");
        }

        if (facts.AuditTrailWired || facts.SoftDeleteWired || facts.MultiTenancyWired || facts.DataProtectionWired || facts.LockingWired)
        {
            output.WriteLine("  → the worker composes the solution's cross-cutting features on its OWN context (audit rows, soft-delete filter, tenant isolation, locks) — nothing to declare, the manifest already says so; the table-owning ones ride `goldpath db add add-worker`");
        }

        return 0;
    }

    private static string ApiPrefix(string apiProject)
    {
        var fileName = Path.GetFileNameWithoutExtension(apiProject);
        return fileName.EndsWith(".Api", StringComparison.Ordinal) ? fileName[..^4] : fileName;
    }

    internal static string Pascal(string name)
    {
        var builder = new StringBuilder(name.Length);
        var upperNext = true;
        foreach (var c in name)
        {
            if (c is '-' or '_' or '.' or ' ')
            {
                upperNext = true;
                continue;
            }

            builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        return builder.Length > 0 ? builder.ToString()
            : throw new CliUsageException($"'{name}' does not yield a project name — use letters (e.g. payments, eod-report).");
    }

    internal static string Kebab(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (char.IsUpper(pascal[i]) && i > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(pascal[i]));
        }

        return builder.ToString();
    }

    private static void WireAppHost(AppFiles files, string projectName, string kebab, string trigger, AppFacts facts)
    {
        var reference = $"    <ProjectReference Include=\"../{projectName}/{projectName}.csproj\" />";
        File.WriteAllText(files.AppHostProject,
            TextEdits.InsertAfterAnchor(File.ReadAllText(files.AppHostProject), Anchors.WorkerReferences, [reference]));

        var safe = projectName.Replace('.', '_');
        List<string> wiring =
        [
            string.Empty,
            $"builder.AddProject<Projects.{safe}>(\"{kebab}\")",
        ];
        if (trigger is "queue" or "jobs")
        {
            wiring.Add("    .WithReference(database).WaitFor(database)");
        }

        if (trigger == "queue")
        {
            wiring.Add("    .WithReference(messaging).WaitFor(messaging)");
        }

        wiring.Add("    .WithHttpHealthCheck(\"/health/ready\");");
        File.WriteAllText(files.AppHostFile,
            TextEdits.InsertAfterAnchor(File.ReadAllText(files.AppHostFile), Anchors.Workers, wiring));
    }

    private static void Restore(Dictionary<string, string> snapshot, string projectDir)
    {
        foreach (var (path, content) in snapshot)
        {
            File.WriteAllText(path, content);
        }

        if (Directory.Exists(projectDir))
        {
            Directory.Delete(projectDir, recursive: true);
        }
    }

    private static IReadOnlyList<string> NextSteps(string trigger) => trigger switch
    {
        "queue" => [
            "rename WorkItemQueued to the REAL upstream event (broker-bound contracts implement IIntegrationEvent — GP0401)",
            "consuming an event the API publishes? the record moves to a shared <App>.Contracts classlib referenced by BOTH — events are wire contracts, never duplicated (docs/rfc/goldpath-event-contracts.md)",
            "put the real work into the consumer; it commits WITH the inbox bookkeeping — exactly-once by construction",
            "the worker owns its OWN tables' migrations: run `goldpath db add add-worker` and commit the migration",
        ],
        "schedule" => [
            "put the real work into IntervalJob.RunTickAsync (time-abstracted, directly testable); configure Worker:Interval",
            "when the work outgrows a timer (chunks, resume, SLA), move to --trigger jobs — the Jobs module is the landing pad",
        ],
        _ => [
            "replace NightlyReportJob's body with the real aggregation; review the cron and the Deadline (every job has an SLA — GP1302)",
            "the worker runs its OWN fleet (SchedulerName) against the app database — the Api's scheduler is untouched; both consoles ride MapGoldpathJobsAdmin",
            "the shared jobs tables stay the API context's migrations (the D3 exclusion is generated); run `goldpath db add add-worker` for the worker's PRIVATE tables",
        ],
    };

    private static void WriteProject(string projectDir, string projectName, string trigger, AppFacts facts)
    {
        Directory.CreateDirectory(projectDir);
        var provider = facts.DatabaseProvider == "sqlserver"
            ? ("Microsoft.EntityFrameworkCore.SqlServer", "UseSqlServer")
            : ("Npgsql.EntityFrameworkCore.PostgreSQL", "UseNpgsql");

        File.WriteAllText(Path.Combine(projectDir, $"{projectName}.csproj"), Csproj(trigger, provider.Item1, facts));
        File.WriteAllText(Path.Combine(projectDir, "GlobalUsings.cs"), "global using Goldpath;\n");
        File.WriteAllText(Path.Combine(projectDir, "Program.cs"), Inherit(Program(projectName, trigger, facts.ConnectionName, provider.Item2), trigger, facts));

        // Aspire infers the worker's HTTP endpoint FROM launchSettings — without this file
        // the AppHost's WithHttpHealthCheck finds no endpoint and the whole app refuses to
        // start (found composing the CorPay sample). Port: deterministic per project name,
        // clear of the template's own 5241.
        var port = 5300 + Math.Abs(projectName.Sum(c => c * 31)) % 200;
        Directory.CreateDirectory(Path.Combine(projectDir, "Properties"));
        File.WriteAllText(Path.Combine(projectDir, "Properties", "launchSettings.json"),
            $$"""
            {
              "$schema": "https://json.schemastore.org/launchsettings.json",
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "dotnetRunMessages": true,
                  "launchBrowser": false,
                  "applicationUrl": "http://localhost:{{port}}",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }

            """);
        switch (trigger)
        {
            case "queue":
                var work = Path.Combine(projectDir, "WorkItems");
                Directory.CreateDirectory(work);
                File.WriteAllText(Path.Combine(work, "ProcessedWorkItem.cs"), ProcessedWorkItem(projectName));
                File.WriteAllText(Path.Combine(work, "WorkItemQueued.cs"), WorkItemQueued(projectName));
                File.WriteAllText(Path.Combine(work, "WorkDbContext.cs"), InheritModel(WorkDbContext(projectName), facts));
                File.WriteAllText(Path.Combine(work, "WorkItemQueuedHandler.cs"), Consumer(projectName));
                break;
            case "schedule":
                var jobs = Path.Combine(projectDir, "Jobs");
                Directory.CreateDirectory(jobs);
                File.WriteAllText(Path.Combine(jobs, "IntervalJob.cs"), IntervalJob(projectName));
                break;
            default:
                var reports = Path.Combine(projectDir, "Reports");
                Directory.CreateDirectory(reports);
                File.WriteAllText(Path.Combine(reports, "DailyReportRow.cs"), DailyReportRow(projectName));
                File.WriteAllText(Path.Combine(reports, "ReportsDbContext.cs"), InheritModel(ReportsDbContext(projectName), facts));
                File.WriteAllText(Path.Combine(reports, "NightlyReportJob.cs"), NightlyReportJob(projectName));
                break;
        }
    }

    private static string Csproj(string trigger, string providerPackage, AppFacts facts)
    {
        var builder = new StringBuilder("""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <!-- goldpath:worker-head — a web host for probes and the fleet console, never the Api (goldpath add worker) -->

              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Goldpath.Abstractions" />
                <PackageReference Include="Goldpath.ServiceDefaults" />

            """);
        if (trigger is "queue" or "jobs")
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.Data" />""");
            builder.AppendLine($"""    <PackageReference Include="{providerPackage}" />""");
            builder.AppendLine("""    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />""");
        }

        if (trigger == "queue")
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.Messaging" />""");
            builder.AppendLine("""    <PackageReference Include="MassTransit.RabbitMQ" />""");
        }

        if (trigger == "jobs")
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.Jobs" />""");
        }

        // Inherited from the solution (T25 concept parity for the in-solution worker): the
        // head's floor, and the cross-cutting features whose concept exists in a process
        // without business HTTP. Table-owning features need the worker's own context.
        if (facts.AuthWired)
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.Auth" />""");
        }

        if (facts.MultiTenancyWired)
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.MultiTenancy" />""");
        }

        if (facts.DataProtectionWired)
        {
            builder.AppendLine("""    <PackageReference Include="Goldpath.DataProtection" />""");
        }

        if (trigger is "queue" or "jobs")
        {
            if (facts.AuditTrailWired)
            {
                builder.AppendLine("""    <PackageReference Include="Goldpath.AuditTrail" />""");
            }

            if (facts.SoftDeleteWired)
            {
                builder.AppendLine("""    <PackageReference Include="Goldpath.SoftDelete" />""");
            }

            if (facts.LockingWired)
            {
                builder.AppendLine(facts.DatabaseProvider == "sqlserver"
                    ? """    <PackageReference Include="Goldpath.Locking.SqlServer" />"""
                    : """    <PackageReference Include="Goldpath.Locking" />""");
            }
        }

        builder.Append("""
                <PackageReference Include="Goldpath.Analyzers" PrivateAssets="all" />
              </ItemGroup>

            </Project>

            """);
        return builder.ToString();
    }

    /// <summary>
    /// Splices the solution's floor and features into the generated Program: a worker added to
    /// an authed solution is authed (its admin surface sits behind the SAME ops floor), and it
    /// composes the cross-cutting features the solution declares — on its own context for the
    /// table-owning ones (queue/jobs), process-wide for tenancy and data protection.
    /// </summary>
    internal static string Inherit(string program, string trigger, AppFacts facts)
    {
        var context = trigger == "queue" ? "WorkDbContext" : "ReportsDbContext";
        var hasContext = trigger is "queue" or "jobs";
        var registrations = new StringBuilder();
        if (facts.AuthWired)
        {
            registrations.AppendLine(facts.AuthApiKey
                ? "builder.AddGoldpathAuth(o => o.Strategy = GoldpathAuthStrategy.ApiKey);   // the solution's floor — copy its Goldpath:Auth section into THIS project's configuration"
                : "builder.AddGoldpathAuth();   // the solution's floor — copy its Goldpath:Auth section (Authority + Audience) into THIS project's configuration");
        }

        if (facts.MultiTenancyWired)
        {
            registrations.AppendLine("builder.AddGoldpathMultiTenancy();                  // the head resolves the tenant; consumers restore it from message headers");
        }

        if (hasContext && facts.AuditTrailWired)
        {
            registrations.AppendLine($"builder.AddGoldpathAuditTrail<WebApplicationBuilder, {context}>();");
        }

        if (hasContext && facts.SoftDeleteWired)
        {
            registrations.AppendLine("builder.AddGoldpathSoftDelete();");
        }

        if (facts.DataProtectionWired)
        {
            registrations.AppendLine("builder.AddGoldpathDataProtection();");
        }

        if (hasContext && facts.LockingWired)
        {
            registrations.AppendLine(facts.DatabaseProvider == "sqlserver"
                ? $"builder.AddGoldpathSqlServerLocking(o => o.ConnectionName = \"{facts.ConnectionName}\");"
                : $"builder.AddGoldpathLocking(o =>\n{{\n    o.Provider = GoldpathLockProvider.Postgres;     // the lock lives in the app database — zero new infra\n    o.ConnectionName = \"{facts.ConnectionName}\";\n}});");
        }

        if (registrations.Length > 0)
        {
            program = program.Replace(
                "builder.AddGoldpathServiceDefaults();\n",
                "builder.AddGoldpathServiceDefaults();\n// Inherited from the solution (goldpath add worker): the same floor, the same features.\n" + registrations,
                StringComparison.Ordinal);
        }

        var middleware = new StringBuilder();
        if (facts.MultiTenancyWired)
        {
            middleware.AppendLine("app.UseGoldpathMultiTenancy();                      // resolve the tenant BEFORE auth binds to it");
        }

        if (facts.AuthWired)
        {
            middleware.AppendLine("app.UseGoldpathAuth();");
        }

        if (middleware.Length > 0)
        {
            program = program.Replace("var app = builder.Build();\n\n", "var app = builder.Build();\n\n" + middleware, StringComparison.Ordinal);
        }

        if (facts.AuthWired)
        {
            program = program.Replace(
                "app.MapGoldpathJobsAdmin<ReportsDbContext>(exposeUnsecured: true);   // internal fleet console — keep it behind the cluster boundary (H2 opt-out, visible)",
                "app.MapGoldpathJobsAdmin<ReportsDbContext>();   // this worker's fleet, behind the SAME ops floor as the Api (H2)",
                StringComparison.Ordinal);
        }

        return program;
    }

    /// <summary>The model calls the inherited features need on the worker's own context.</summary>
    internal static string InheritModel(string model, AppFacts facts)
    {
        var calls = new StringBuilder();
        if (facts.AuditTrailWired)
        {
            calls.AppendLine("        modelBuilder.AddGoldpathAuditLog();");
        }

        if (facts.SoftDeleteWired)
        {
            calls.AppendLine("        modelBuilder.ApplyGoldpathSoftDelete();");
        }

        if (facts.MultiTenancyWired)
        {
            calls.AppendLine("        modelBuilder.ApplyGoldpathMultiTenancy(this);   // context-rooted ON PURPOSE — keeps the filter live");
        }

        return calls.Length == 0
            ? model
            : model.Replace("        modelBuilder.ApplyGoldpathModelDefaults();\n", "        modelBuilder.ApplyGoldpathModelDefaults();\n        // Inherited from the solution (goldpath add worker): the same rows on this context.\n" + calls, StringComparison.Ordinal);
    }

    private static string Program(string ns, string trigger, string? connection, string useProvider) => trigger switch
    {
        "queue" => $$"""
            using {{ns}}.WorkItems;
            using MassTransit;
            using Microsoft.EntityFrameworkCore;

            // A web host on purpose: readiness/liveness probes are the deployment contract of a
            // worker too — the HTTP surface carries probes, never business APIs.
            var builder = WebApplication.CreateBuilder(args);

            builder.AddGoldpathServiceDefaults();

            // Connection strings come from the AppHost; configuration stays tolerant, usage fails loudly.
            var workDbConnection = builder.Configuration.GetConnectionString("{{connection}}");
            builder.AddGoldpathData<WebApplicationBuilder, WorkDbContext>(options =>
            {
                // Design time (`dotnet ef`): the provider must BIND without a connection.
                if (workDbConnection is not null)
                {
                    options.{{useProvider}}(workDbConnection);
                }
                else
                {
                    options.{{useProvider}}();
                }
            });

            builder.AddGoldpathMessaging(bus =>
            {
                bus.AddGoldpathHandler<WorkItemQueued, WorkItemQueuedHandler>();   // the consume seam (ADR-0013): no bus type in the handler
                // Consumer-side INBOX: every receive endpoint dedups on MessageId — exactly-once processing.
                bus.AddGoldpathOutbox<WorkDbContext>(outbox => outbox.{{(useProvider == "UseNpgsql" ? "UsePostgres" : "UseSqlServer")}}());
                bus.UsingRabbitMq((context, cfg) =>
                {
                    if (builder.Configuration.GetConnectionString("messaging") is { } messagingConnection)
                    {
                        cfg.Host(new Uri(messagingConnection));
                    }

                    cfg.ConfigureGoldpathEndpoints(context);
                });
            });

            var app = builder.Build();

            app.MapGoldpathDefaultEndpoints();
            // Smoke-visible read model (what has been processed) — intentionally the only endpoint.
            app.MapGet("/api/v1/processed", async (WorkDbContext db) =>
                await db.ProcessedWorkItems.OrderBy(w => w.ProcessedAt).ToListAsync());

            app.Run();

            """,
        "schedule" => $$"""
            using {{ns}}.Jobs;

            // A web host on purpose: readiness/liveness probes are the deployment contract of a
            // worker too — the HTTP surface carries probes, never business APIs.
            var builder = WebApplication.CreateBuilder(args);

            builder.AddGoldpathServiceDefaults();

            builder.Services.AddSingleton<IntervalJob>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<IntervalJob>());

            var app = builder.Build();

            app.MapGoldpathDefaultEndpoints();
            // Smoke-visible tick counter — intentionally the only endpoint.
            app.MapGet("/api/v1/ticks", (IntervalJob job) => new { job.TickCount });

            app.Run();

            """,
        _ => $$"""
            using {{ns}}.Reports;
            using Microsoft.EntityFrameworkCore;

            // A web host on purpose: readiness/liveness probes are the deployment contract of a
            // worker too — the HTTP surface carries probes (and the jobs console), never business APIs.
            var builder = WebApplication.CreateBuilder(args);

            builder.AddGoldpathServiceDefaults();

            // Connection strings come from the AppHost; configuration stays tolerant, usage fails loudly.
            var reportsDbConnection = builder.Configuration.GetConnectionString("{{connection}}");
            builder.AddGoldpathData<WebApplicationBuilder, ReportsDbContext>(options =>
            {
                // Design time (`dotnet ef`): the provider must BIND without a connection.
                if (reportsDbConnection is not null)
                {
                    options.{{useProvider}}(reportsDbConnection);
                }
                else
                {
                    options.{{useProvider}}();
                }
            });

            // Clustered jobs (Goldpath.Jobs) on the APP database, as this worker's OWN fleet: the
            // SchedulerName separates it from the Api's scheduler — same tables, two clusters,
            // zero contention for fires (one scheduler per PROCESS, one fleet per PURPOSE).
            builder.AddGoldpathJobs<WebApplicationBuilder, ReportsDbContext>(jobs =>
            {
                jobs.ConnectionName = "{{connection}}";
                jobs.SchedulerName = "{{ns.ToLowerInvariant().Replace('.', '-')}}";
                jobs.AddJob<NightlyReportJob>(j =>
                {
                    j.Cron = "0 0 1 * * ?";                    // nightly at 01:00
                    j.Deadline = TimeSpan.FromHours(2);        // every job has an SLA (GP1302)
                    j.MaxParallelChunks = 2;
                });
            });

            var app = builder.Build();

            app.MapGoldpathDefaultEndpoints();
            app.MapGoldpathJobsAdmin<ReportsDbContext>(exposeUnsecured: true);   // internal fleet console — keep it behind the cluster boundary (H2 opt-out, visible)

            app.Run();

            """,
    };

    private static string ProcessedWorkItem(string ns) => $$"""
        namespace {{ns}}.WorkItems;

        /// <summary>The durable result of one processed message — the walking skeleton's "work done" proof.</summary>
        public class ProcessedWorkItem
        {
            /// <summary>The upstream work-item identity (also the primary key: a natural dedup backstop).</summary>
            public Guid Id { get; set; }

            /// <summary>What was processed.</summary>
            public string Payload { get; set; } = string.Empty;

            /// <summary>When processing committed (UTC policy: DateTimeOffset).</summary>
            public DateTimeOffset ProcessedAt { get; set; }
        }

        """;

    private static string WorkItemQueued(string ns) => $$"""
        namespace {{ns}}.WorkItems;

        /// <summary>
        /// The broker-bound contract this worker drains (implements <c>IIntegrationEvent</c> —
        /// GP0401). Rename/replace it with the real upstream event.
        /// </summary>
        public record WorkItemQueued(Guid WorkItemId, string Payload) : IIntegrationEvent;

        """;

    private static string WorkDbContext(string ns) => $$"""
        using MassTransit;
        using Microsoft.EntityFrameworkCore;

        namespace {{ns}}.WorkItems;

        public class WorkDbContext(DbContextOptions<WorkDbContext> options) : DbContext(options)
        {
            public DbSet<ProcessedWorkItem> ProcessedWorkItems => Set<ProcessedWorkItem>();

            protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
                => configurationBuilder.ApplyGoldpathConventions();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyGoldpathModelDefaults();

                // Inbox/outbox tables: the consumer-side dedup store (exactly-once processing).
                modelBuilder.AddInboxStateEntity();
                modelBuilder.AddOutboxMessageEntity();
                modelBuilder.AddOutboxStateEntity();
            }
        }

        """;

    private static string Consumer(string ns) => $$"""
        namespace {{ns}}.WorkItems;

        /// <summary>
        /// The walking-skeleton handler: inbox-guarded (exactly-once), commits its result in the
        /// same transaction as the dedup bookkeeping. Through the seam (ADR-0013): no bus type
        /// here; the queue is named after the handler (work-item-queued). Replace the body with
        /// the real work.
        /// </summary>
        public class WorkItemQueuedHandler(WorkDbContext db) : IIntegrationEventHandler<WorkItemQueued>
        {
            /// <inheritdoc />
            public async Task HandleAsync(WorkItemQueued integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
            {
                db.ProcessedWorkItems.Add(new ProcessedWorkItem
                {
                    Id = integrationEvent.WorkItemId,
                    Payload = integrationEvent.Payload,
                    ProcessedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        """;

    private static string IntervalJob(string ns) => $$"""
        namespace {{ns}}.Jobs;

        /// <summary>
        /// BCL <see cref="PeriodicTimer"/> skeleton (template-completion RFC D3): dependency-free
        /// scheduling that the Jobs module later replaces without touching the host shape. Put the
        /// actual work in <see cref="RunTickAsync"/>; keep the timer loop free of business logic.
        /// </summary>
        public sealed class IntervalJob(ILogger<IntervalJob> logger, IConfiguration configuration) : BackgroundService
        {
            /// <summary>Ticks executed so far (smoke-observable via /api/v1/ticks).</summary>
            public int TickCount { get; private set; }

            /// <inheritdoc />
            protected override async Task ExecuteAsync(CancellationToken stoppingToken)
            {
                var interval = configuration.GetValue("Worker:Interval", TimeSpan.FromMinutes(1));
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await RunTickAsync();
                }
            }

            /// <summary>One unit of scheduled work — replace the log line with the real job.</summary>
            public Task RunTickAsync()
            {
                TickCount++;
                logger.LogInformation("Interval tick {TickCount} executed.", TickCount);
                return Task.CompletedTask;
            }
        }

        """;

    private static string DailyReportRow(string ns) => $$"""
        namespace {{ns}}.Reports;

        /// <summary>One summarized day — the walking skeleton's "chunk did real work" proof.</summary>
        public class DailyReportRow
        {
            /// <summary>Day offset the row summarizes (the job's range payloads walk these).</summary>
            public int DayOffset { get; set; }

            /// <summary>When the summary was (re)generated (UTC policy: DateTimeOffset).</summary>
            public DateTimeOffset GeneratedAt { get; set; }
        }

        """;

    private static string ReportsDbContext(string ns) => $$"""
        using Microsoft.EntityFrameworkCore;

        namespace {{ns}}.Reports;

        public class ReportsDbContext(DbContextOptions<ReportsDbContext> options) : DbContext(options)
        {
            public DbSet<DailyReportRow> DailyReports => Set<DailyReportRow>();

            protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
                => configurationBuilder.ApplyGoldpathConventions();

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.ApplyGoldpathModelDefaults();
                modelBuilder.Entity<DailyReportRow>(report =>
                {
                    report.HasKey(r => r.DayOffset);
                    // The key IS the day — never an identity (day 0 must not become "generated").
                    report.Property(r => r.DayOffset).ValueGeneratedNever();
                });

                // SHARED tables with the Api's fleet: the SchedulerName in Program.cs keeps
                // the clusters apart, and the API'S context OWNS their migrations — this head
                // maps them for querying only (one table set, ONE owner: migrations RFC D3).
                modelBuilder.AddGoldpathJobs(excludeFromMigrations: true);
            }
        }

        """;

    private static string NightlyReportJob(string ns) => $$"""
        using Microsoft.EntityFrameworkCore;
        using Microsoft.Extensions.DependencyInjection;

        namespace {{ns}}.Reports;

        /// <summary>
        /// The walking-skeleton job: summarizes the last 30 days in 5-day CHUNKS. After every chunk
        /// the runner checkpoints — kill the pod mid-run and another node resumes where it stopped
        /// (never from the start). Replace the body with the real aggregation.
        /// </summary>
        public sealed class NightlyReportJob : IGoldpathJob
        {
            /// <inheritdoc />
            public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
                => Task.FromResult(GoldpathJobPlanner.ByRange(totalItems: 30, chunkSize: 5));   // count, never materialize (GP1303)

            /// <inheritdoc />
            public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
            {
                var db = context.Services.GetRequiredService<ReportsDbContext>();
                var (start, endExclusive) = GoldpathJobPlanner.ParseRange(chunk.Payload);
                for (var dayOffset = (int)start; dayOffset < endExclusive; dayOffset++)
                {
                    var row = await db.DailyReports.FindAsync([dayOffset], cancellationToken)
                        ?? db.DailyReports.Add(new DailyReportRow { DayOffset = dayOffset }).Entity;
                    row.GeneratedAt = DateTimeOffset.UtcNow;
                }

                await db.SaveChangesAsync(cancellationToken);   // one batched write per chunk (house rule)
            }
        }

        """;
}
