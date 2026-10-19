using System.Text.RegularExpressions;

namespace Goldpath.Cli;

/// <summary>Everything one feature changes in an app — the CLI mirror of the drift profile's row.</summary>
public sealed class RecipePlan
{
    /// <summary>The manifest key under <c>features:</c> (already-enabled detection).</summary>
    public required string ManifestKey { get; init; }

    /// <summary>Package references for the Api project.</summary>
    public List<string> ApiPackages { get; } = [];

    /// <summary>Package references for the AppHost project.</summary>
    public List<string> AppHostPackages { get; } = [];

    /// <summary>Builder registrations (Program.cs, registrations anchor).</summary>
    public List<string> Registrations { get; } = [];

    /// <summary>Middleware lines (Program.cs, middleware anchor — before auth by anchor position).</summary>
    public List<string> Middleware { get; } = [];

    /// <summary>Endpoint-mapping lines (Program.cs, endpoints anchor — admin surfaces, after auth).</summary>
    public List<string> Endpoints { get; } = [];

    /// <summary>
    /// Lines inserted INSIDE an existing <c>AddGoldpathJobs</c> configuration (after its
    /// ConnectionName line) — jobs-riding features compose into ONE scheduler, never two.
    /// </summary>
    public List<string> JobsOptionsLines { get; } = [];

    /// <summary>
    /// Lines inserted INSIDE an existing <c>AddGoldpathMessaging</c> configuration (after the
    /// consumers anchor) — bus-riding features register on THE app's bus, never a second one.
    /// </summary>
    public List<string> BusLines { get; } = [];

    /// <summary>
    /// Scalars to set under <c>providers:</c> — a recipe that births infrastructure must say
    /// so in the manifest too (the outbox that brings the broker flips <c>broker: none</c>),
    /// or the engine's own rule (SPEC0101) refuses the result it just produced.
    /// </summary>
    public List<(string Key, string Value)> ProviderEdits { get; } = [];

    /// <summary>
    /// Central pins (<c>Directory.Packages.props</c>) a recipe must add for packages the
    /// template pins only under a symbol the app lacks — see <see cref="PackagePins"/>.
    /// </summary>
    public List<(string Package, string Version)> PackageVersions { get; } = [];

    /// <summary>
    /// Usings the MODEL file needs for the model calls (the outbox tables are MassTransit
    /// extension methods; the template's own using sits behind UseBroker). Found by the
    /// GmGrown shape (2026-09-04): the calls landed, the using did not — CS1061.
    /// </summary>
    public List<string> ModelUsings { get; } = [];

    /// <summary>Model calls (OnModelCreating, model anchor; pre-indented).</summary>
    public List<string> ModelCalls { get; } = [];

    /// <summary>AppHost resource lines (resources anchor).</summary>
    public List<string> Resources { get; } = [];

    /// <summary>AppHost reference-chain lines (references anchor; pre-indented).</summary>
    public List<string> References { get; } = [];

    /// <summary>Manifest lines under <c>features:</c> (pre-indented).</summary>
    public List<string> ManifestLines { get; } = [];

    /// <summary>Markers whose lines are removed from Program.cs (retired fallbacks).</summary>
    public List<string> RemoveFromProgram { get; } = [];

    /// <summary>Namespaces Program.cs must import for the emitted lines to compile (idempotent).</summary>
    public List<string> Usings { get; } = [];

    /// <summary>
    /// Lines placed directly above the template's sample command (<see cref="AppFiles.SampleCommandFile"/>),
    /// exactly as the template emits them under the same feature. Template-owned code only —
    /// the team's own commands are <see cref="NextSteps"/>. Skipped when the sample is gone.
    /// </summary>
    public List<string> SampleCommandLines { get; } = [];

    /// <summary>
    /// Package references for the project that compiles the sample command, when that is NOT
    /// the packages project (clean-architecture's Application project).
    /// </summary>
    public List<string> SampleCommandPackages { get; } = [];

    /// <summary>
    /// Lines placed directly after the template's smoke-test client (<see cref="AppFiles.SmokeTestFile"/>),
    /// exactly as the template emits them under the same feature — the smoke is template-owned
    /// and must keep passing in a grown app. Skipped when the smoke is gone.
    /// </summary>
    public List<string> SmokeClientLines { get; } = [];

    /// <summary>Namespaces the smoke test must import for <see cref="SmokeClientLines"/> to compile.</summary>
    public List<string> SmokeUsings { get; } = [];

    /// <summary>Package references the smoke-test project needs for <see cref="SmokeClientLines"/>.</summary>
    public List<string> SmokePackages { get; } = [];

    /// <summary>Text whose presence in the smoke means the team already did it — nothing is touched then.</summary>
    public string? SmokeDoneMarker { get; set; }

    /// <summary>Domain opt-ins the team decides — printed, never guessed.</summary>
    public List<string> NextSteps { get; } = [];
}

/// <summary>What the recipes read from the app before deciding their lines.</summary>
public sealed class AppFacts
{
    /// <summary>The DbContext class name (audit registration is generic over it).</summary>
    public required string DbContextName { get; init; }

    /// <summary>EF provider detected from the Api csproj: postgres | sqlserver | none.</summary>
    public required string DatabaseProvider { get; init; }

    /// <summary>The app's database connection name (locking reuses the app database).</summary>
    public required string? ConnectionName { get; init; }

    /// <summary>Whether AddGoldpathCaching is already wired (idempotency store decision).</summary>
    public required bool CachingWired { get; init; }

    /// <summary>Whether AddGoldpathJobs is already wired (a second jobs composition would double the scheduler).</summary>
    public required bool JobsWired { get; init; }

    /// <summary>Whether AddGoldpathMessaging is already wired (campaign requires the broker seam, RFC D8).</summary>
    public required bool MessagingWired { get; init; }

    /// <summary>Whether AddGoldpathAuth is wired (admin surfaces: policy by default, VISIBLE opt-out without).</summary>
    public required bool AuthWired { get; init; }

    /// <summary>
    /// Whether MapGoldpathConsole is already mapped: the FIRST jobs-riding feature brings
    /// the operations console with it (exactly as the template does); later ones find it.
    /// </summary>
    public bool ConsoleWired { get; init; }

    /// <summary>Whether the auth floor is the API-key strategy (the worker's head mirrors it).</summary>
    public bool AuthApiKey { get; init; }

    /// <summary>The solution's cross-cutting features an added worker INHERITS on its own context (open-threads T25 → add-worker parity, 2026-09-05).</summary>
    public bool AuditTrailWired { get; init; }

    /// <inheritdoc cref="AuditTrailWired"/>
    public bool SoftDeleteWired { get; init; }

    /// <inheritdoc cref="AuditTrailWired"/>
    public bool MultiTenancyWired { get; init; }

    /// <inheritdoc cref="AuditTrailWired"/>
    public bool DataProtectionWired { get; init; }

    /// <inheritdoc cref="AuditTrailWired"/>
    public bool LockingWired { get; init; }

    /// <summary>The Aspire hosting version the app pins (from <c>Aspire.Hosting.AppHost</c>), or null without central pins.</summary>
    public string? AspireVersion { get; init; }

    /// <summary>The Goldpath train the app pins (from <c>Goldpath.Abstractions</c>), or null without central pins.</summary>
    public string? TrainVersion { get; init; }

    /// <summary>The Mediant line the app pins (from <c>Mediant.AspNetCore</c>), or null without central pins.</summary>
    public string? MediantVersion { get; init; }

    /// <summary>
    /// Whether the template's sample command compiles in a project OTHER than the packages
    /// project — clean-architecture's Application project, which does not reach
    /// <c>Mediant.Behaviors</c> through <c>Goldpath.Idempotency</c> the way the Api project does.
    /// </summary>
    public bool SampleCommandInOwnProject { get; init; }

    /// <summary>Reads the context facts from the located files.</summary>
    public static AppFacts Read(AppFiles files)
    {
        var props = files.PackagesProps is { } propsPath ? File.ReadAllText(propsPath) : "";
        var model = File.ReadAllText(files.ModelFile);
        var program = File.ReadAllText(files.ProgramFile);
        var apiProject = File.ReadAllText(files.PackagesProject);

        var dbContext = Regex.Match(model, @"class\s+(\w+)");
        if (!dbContext.Success)
        {
            throw new CliFailureException($"no class declaration found in {files.ModelFile} — cannot infer the DbContext type.");
        }

        var provider = apiProject.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal) ? "postgres"
            : apiProject.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.Ordinal) ? "sqlserver"
            : "none";

        var connection = Regex.Match(program, "GetConnectionString\\(\"([^\"]+)\"\\)");

        return new AppFacts
        {
            DbContextName = dbContext.Groups[1].Value,
            DatabaseProvider = provider,
            ConnectionName = connection.Success ? connection.Groups[1].Value : null,
            CachingWired = program.Contains("AddGoldpathCaching(", StringComparison.Ordinal),
            JobsWired = program.Contains("builder.AddGoldpathJobs<", StringComparison.Ordinal),
            MessagingWired = program.Contains("builder.AddGoldpathMessaging(", StringComparison.Ordinal),
            AuthWired = program.Contains("builder.AddGoldpathAuth(", StringComparison.Ordinal),
            ConsoleWired = program.Contains("app.MapGoldpathConsole(", StringComparison.Ordinal),
            AuthApiKey = program.Contains("GoldpathAuthStrategy.ApiKey", StringComparison.Ordinal),
            AuditTrailWired = program.Contains("builder.AddGoldpathAuditTrail<", StringComparison.Ordinal),
            SoftDeleteWired = program.Contains("builder.AddGoldpathSoftDelete(", StringComparison.Ordinal),
            MultiTenancyWired = program.Contains("builder.AddGoldpathMultiTenancy(", StringComparison.Ordinal),
            DataProtectionWired = program.Contains("builder.AddGoldpathDataProtection(", StringComparison.Ordinal),
            LockingWired = program.Contains("builder.AddGoldpathLocking(", StringComparison.Ordinal) || program.Contains("builder.AddGoldpathSqlServerLocking(", StringComparison.Ordinal),
            AspireVersion = PackagePins.Read(props, "Aspire.Hosting.AppHost"),
            TrainVersion = PackagePins.Read(props, "Goldpath.Abstractions"),
            MediantVersion = PackagePins.Read(props, "Mediant.AspNetCore"),
            SampleCommandInOwnProject = files.SampleCommandProject is { } owner
                && !string.Equals(Path.GetFullPath(owner), Path.GetFullPath(files.PackagesProject), StringComparison.Ordinal),
        };
    }
}

/// <summary>
/// The THIRTEEN feature recipes — nine Ring B cross-cutting features (multitenancy,
/// audittrail, softdelete, idempotency, dataprotection, caching, locking, approvals,
/// fileexchange) plus the four execution-ladder modules (archival, bulk, notification,
/// campaign), which the CLI wires the same way even though they are not Ring B. Every line
/// mirrors what <c>dotnet new goldpath-solution --features X</c> would have generated — the
/// CLI adds nothing the template would not; specdrift stays the acceptance test for both
/// paths.
/// </summary>
public static class FeatureRecipes
{
    /// <summary>The one recipe that is not a template <c>--features</c> choice.</summary>
    public const string OutboxRecipe = "outbox";

    /// <summary>The feature names <c>goldpath add feature</c> understands.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["multitenancy", "audittrail", "softdelete", "idempotency", "dataprotection", "caching", "locking", "approvals", "fileexchange", "archival", "bulk", "notification", "campaign", "outbox"];

    /// <summary>
    /// The features the SOLUTION TEMPLATE's <c>--features</c> choice list accepts — every
    /// recipe except <c>outbox</c>, which no template flag can express (a generated app is
    /// born with the bus through <c>--broker rabbitmq</c>, and the recipe BIRTHS one in an
    /// app that has none). The wizard's menu is this list, not <see cref="Names"/>: a menu
    /// offering outbox produced <c>dotnet new goldpath-solution --features outbox</c>, which
    /// the template rejects (found by the preview.8 coverage audit, 2026-09-05).
    /// </summary>
    public static IReadOnlyList<string> TemplateFeatures { get; } =
        Names.Where(n => n != OutboxRecipe).ToList();

    /// <summary>The jobs-riding features — the ones that bring the operations console with them.</summary>
    private static readonly HashSet<string> JobsRiders = new(StringComparer.Ordinal)
        { "approvals", "fileexchange", "archival", "bulk", "notification", "campaign" };

    /// <summary>Builds the plan for one feature against the app's read context.</summary>
    public static RecipePlan Build(string feature, AppFacts app)
    {
        var plan = BuildCore(feature, app);

        // The console rides the FIRST jobs-riding feature, exactly as the template composes
        // it (any operational module → MapGoldpathConsole): a CLI-grown app must not end up
        // with the admin API and no screen over it. Later riders find it already mapped.
        if (JobsRiders.Contains(feature) && !app.ConsoleWired)
        {
            plan.ApiPackages.Add("Goldpath.Console");
            plan.Endpoints.Add(app.AuthWired
                ? "app.MapGoldpathConsole();                           // behind the SAME ops floor as the surfaces"
                : "app.MapGoldpathConsole(exposeUnsecured: true);      // the console over the surfaces above — visible opt-out, acceptable only behind an authenticating boundary");
        }

        return plan;
    }

    private static RecipePlan BuildCore(string feature, AppFacts app)
    {
        switch (feature)
        {
            case "outbox":
                {
                    // The transactional outbox rides the app's bus. Two shapes: the bus is
                    // already composed (an init'd brownfield app, a `--broker` template with
                    // the outbox stripped) → the outbox joins it; no bus → the bus is born
                    // here with the broker resource, exactly as `--broker rabbitmq` generates.
                    var plan = new RecipePlan { ManifestKey = "outbox" };
                    string providerLine = app.DatabaseProvider switch
                    {
                        "postgres" => "        outbox.UsePostgres();",
                        "sqlserver" => "        outbox.UseSqlServer();",
                        _ => throw new CliFailureException("no EF provider reference found in the Api project — the outbox tables live in the app database and need one."),
                    };
                    plan.ApiPackages.Add("Goldpath.Messaging");
                    plan.Usings.Add("MassTransit");
                    if (app.MessagingWired)
                    {
                        plan.BusLines.Add("    bus.AddGoldpathOutbox<" + app.DbContextName + ">(outbox =>");
                        plan.BusLines.Add("    {");
                        plan.BusLines.Add(providerLine);
                        plan.BusLines.Add("    });");
                    }
                    else
                    {
                        plan.ApiPackages.Add("MassTransit.RabbitMQ");
                        plan.AppHostPackages.Add("Aspire.Hosting.RabbitMQ");
                        // The manifest must tell the same story as the AppHost: a bus born here
                        // is a broker the app now depends on. Found by the GmGrown shape
                        // (2026-09-04): with `broker: none` left in place the engine's SPEC0101
                        // refused the recipe's own result and rolled everything back.
                        plan.ProviderEdits.Add(("broker", "rabbitmq"));
                        // A `--broker none` app pins none of the bus packages (the template
                        // pins them under UseBroker), so the born bus brings its pins.
                        var train = app.TrainVersion
                            ?? throw new CliFailureException("no central pin for Goldpath.Abstractions found in Directory.Packages.props — the born bus must pin Goldpath.Messaging on the app's train, and the CLI reads the train from that line.");
                        var aspire = app.AspireVersion
                            ?? throw new CliFailureException("no central pin for Aspire.Hosting.AppHost found in Directory.Packages.props — the broker resource needs Aspire.Hosting.RabbitMQ on the same Aspire line.");
                        plan.PackageVersions.Add(("Goldpath.Messaging", train));
                        plan.PackageVersions.Add(("MassTransit.RabbitMQ", KnownVersions.MassTransitRabbitMq));
                        plan.PackageVersions.Add(("Aspire.Hosting.RabbitMQ", aspire));
                        plan.Resources.Add("var messaging = builder.AddRabbitMQ(\"messaging\");");
                        plan.References.Add("    .WithReference(messaging).WaitFor(messaging)");
                        plan.Registrations.Add("builder.AddGoldpathMessaging(bus =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add("    // goldpath:features consumers — bus-riding features register here");
                        plan.Registrations.Add("    bus.AddGoldpathOutbox<" + app.DbContextName + ">(outbox =>");
                        plan.Registrations.Add("    {");
                        plan.Registrations.Add(providerLine);
                        plan.Registrations.Add("    });");
                        plan.Registrations.Add("    bus.UsingRabbitMq((context, cfg) =>");
                        plan.Registrations.Add("    {");
                        plan.Registrations.Add("        if (builder.Configuration.GetConnectionString(\"messaging\") is { } messagingConnection)");
                        plan.Registrations.Add("        {");
                        plan.Registrations.Add("            cfg.Host(new Uri(messagingConnection));");
                        plan.Registrations.Add("        }");
                        plan.Registrations.Add("");
                        plan.Registrations.Add("        cfg.ConfigureGoldpathEndpoints(context);");
                        plan.Registrations.Add("    });");
                        plan.Registrations.Add("});");
                    }

                    plan.ModelUsings.Add("MassTransit");
                    plan.ModelCalls.Add("        // Transactional outbox/inbox tables (features.outbox in the manifest).");
                    plan.ModelCalls.Add("        modelBuilder.AddInboxStateEntity();");
                    plan.ModelCalls.Add("        modelBuilder.AddOutboxMessageEntity();");
                    plan.ModelCalls.Add("        modelBuilder.AddOutboxStateEntity();");
                    plan.ManifestLines.Add("  outbox: true");
                    plan.NextSteps.Add("publish through IIntegrationEventPublisher — events implement IIntegrationEvent and leave in the SAME transaction as the write (never a direct bus publish: GP0401)");
                    plan.NextSteps.Add("register consumers at the `goldpath:features consumers` anchor inside AddGoldpathMessaging");
                    plan.NextSteps.Add("configure ConnectionStrings:messaging (the AppHost injects it in Development)");
                    return plan;
                }

            case "multitenancy":
                {
                    var plan = new RecipePlan { ManifestKey = "multiTenancy" };
                    plan.ApiPackages.Add("Goldpath.MultiTenancy");
                    plan.Registrations.Add("builder.AddGoldpathMultiTenancy();");
                    plan.Middleware.Add("app.UseGoldpathMultiTenancy();                      // resolve the tenant BEFORE auth binds to it");
                    plan.ModelCalls.Add("        modelBuilder.ApplyGoldpathMultiTenancy(this);   // context-rooted ON PURPOSE — keeps the filter live");
                    plan.ManifestLines.Add("  multiTenancy: true");

                    // Fail-closed tenancy refuses a request without the header (400) before auth
                    // runs, so the template's own smoke sends it under UseMultiTenancy. Without it
                    // an app grown with this verb failed its own smoke — the nightly GmGrownRest
                    // shape, 2026-09-14. Same line, same using, same reference as the template.
                    plan.SmokeClientLines.Add("        client.DefaultRequestHeaders.Add(GoldpathHeaders.TenantId, \"smoke-tenant\");   // fail-closed tenancy");
                    plan.SmokeUsings.Add("Goldpath");
                    plan.SmokePackages.Add("Goldpath.Abstractions");
                    plan.SmokeDoneMarker = "GoldpathHeaders.TenantId";
                    plan.NextSteps.Add("mark tenant-owned entities: partial class X : IMultiTenant (TenantId is filtered + write-guarded)");
                    plan.NextSteps.Add("fail-closed from now on: every request (and test) must send the Goldpath-Tenant-Id header (GoldpathHeaders.TenantId)");
                    return plan;
                }

            case "audittrail":
                {
                    var plan = new RecipePlan { ManifestKey = "auditTrail" };
                    plan.ApiPackages.Add("Goldpath.AuditTrail");
                    plan.Registrations.Add($"builder.AddGoldpathAuditTrail<WebApplicationBuilder, {app.DbContextName}>();");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathAuditLog();");
                    plan.ManifestLines.Add("  auditTrail: true");
                    plan.NextSteps.Add("mark audited entities: partial class X : IAuditLogged (change rows commit in the same transaction)");
                    return plan;
                }

            case "softdelete":
                {
                    var plan = new RecipePlan { ManifestKey = "softDelete" };
                    plan.ApiPackages.Add("Goldpath.SoftDelete");
                    plan.Registrations.Add("builder.AddGoldpathSoftDelete();");
                    plan.ModelCalls.Add("        modelBuilder.ApplyGoldpathSoftDelete();");
                    plan.ManifestLines.Add("  softDelete: true");
                    plan.NextSteps.Add("mark soft-deletable entities: partial class X : ISoftDeletable (deletes become stamped updates)");
                    return plan;
                }

            case "idempotency":
                {
                    var plan = new RecipePlan { ManifestKey = "idempotency" };
                    plan.ApiPackages.Add("Goldpath.Idempotency");
                    if (!app.CachingWired)
                    {
                        plan.Registrations.Add("builder.Services.AddDistributedMemoryCache();  // idempotency store fallback; enable caching for Redis-backed keys");
                    }

                    plan.Registrations.Add("builder.AddGoldpathIdempotency();");
                    plan.ManifestLines.Add("  idempotency: true");

                    // The template marks its own sample command under UseIdempotency, and GP1001
                    // fails the build while any command is unmarked. Without these lines an app
                    // grown with this verb stopped building on its first `dotnet build` — the
                    // nightly GmGrownRest shape, 2026-09-14. Same four lines as the template.
                    plan.SampleCommandLines.AddRange(
                    [
                        "// The golden path marks every write-performing command (GP1001 holds the composition to",
                        "// it): a client retry replays the stored answer instead of creating a second order, and",
                        "// the business reference — not the whole payload — is the key.",
                        "[Mediant.Behaviors.Attributes.Idempotent(KeyProperty = nameof(CreateOrderCommand.Reference))]",
                    ]);
                    if (app.SampleCommandInOwnProject)
                    {
                        var mediant = app.MediantVersion
                            ?? throw new CliFailureException("no central pin for Mediant.AspNetCore found in Directory.Packages.props — the sample command's project needs Mediant.Behaviors on the app's Mediant line.");
                        plan.SampleCommandPackages.Add("Mediant.Behaviors");
                        plan.PackageVersions.Add(("Mediant.Behaviors", mediant));
                    }

                    plan.NextSteps.Add("mark every other write-performing command [Idempotent] — GP1001 fails the build until each one is (the template's sample CreateOrderCommand is marked for you); clients send the Idempotency-Key header");
                    return plan;
                }

            case "dataprotection":
                {
                    var plan = new RecipePlan { ManifestKey = "dataProtection" };
                    plan.ApiPackages.Add("Goldpath.DataProtection");
                    plan.Registrations.Add("builder.AddGoldpathDataProtection();");
                    plan.ManifestLines.Add("  dataProtection: true");
                    plan.NextSteps.Add("classify once: [GoldpathPersonalData] on sensitive properties — every sink (audit rows, logs) masks them");
                    return plan;
                }

            case "caching":
                {
                    var plan = new RecipePlan { ManifestKey = "distributedCaching" };
                    plan.ApiPackages.Add("Goldpath.Caching");
                    plan.AppHostPackages.Add("Aspire.Hosting.Redis");
                    plan.Registrations.Add("builder.AddGoldpathCaching();                       // HybridCache L1+L2 (redis resource in the AppHost)");
                    plan.Resources.Add("var cache = builder.AddRedis(\"redis\");");
                    plan.References.Add("    .WithReference(cache).WaitFor(cache)");
                    plan.ManifestLines.Add("  distributedCaching: true");
                    plan.RemoveFromProgram.Add("idempotency store fallback");
                    plan.NextSteps.Add("mark queries [Cacheable] and the commands that change them [InvalidatesCache] — one tag vocabulary");
                    return plan;
                }

            case "locking":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — locking reuses the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "distributedLocking" };
                    switch (app.DatabaseProvider)
                    {
                        case "postgres":
                            plan.ApiPackages.Add("Goldpath.Locking");
                            plan.Registrations.Add("builder.AddGoldpathLocking(o =>");
                            plan.Registrations.Add("{");
                            plan.Registrations.Add("    o.Provider = GoldpathLockProvider.Postgres;     // the lock lives in the app database — zero new infra");
                            plan.Registrations.Add($"    o.ConnectionName = \"{connection}\";");
                            plan.Registrations.Add("});");
                            break;
                        case "sqlserver":
                            plan.ApiPackages.Add("Goldpath.Locking.SqlServer");
                            plan.Registrations.Add($"builder.AddGoldpathSqlServerLocking(o => o.ConnectionName = \"{connection}\");");
                            break;
                        default:
                            throw new CliFailureException("no EF provider reference found in the Api project — locking lives in the app database and needs one.");
                    }

                    plan.ManifestLines.Add("  distributedLocking:");
                    plan.ManifestLines.Add($"    provider: {app.DatabaseProvider}");
                    plan.ManifestLines.Add($"    connectionName: {connection}");
                    plan.NextSteps.Add("take locks through IGoldpathLockFactory — never raw connections; see the Goldpath.Locking README");
                    return plan;
                }

            case "approvals":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — the escalation sweep runs on Jobs, which lives in the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "approvals" };
                    plan.ApiPackages.Add("Goldpath.Approvals");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    jobs.AddGoldpathApprovalsJobs();               // escalation sweep — overdue rungs move up, the top rung expires");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add("    jobs.AddGoldpathApprovalsJobs();               // escalation sweep — overdue rungs move up, the top rung expires");
                        plan.Registrations.Add("});");
                    }

                    plan.Registrations.Add($"builder.AddGoldpathApprovals<WebApplicationBuilder, {app.DbContextName}>(approvals =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR authority chains here (goldpath never guesses who may approve):");
                    plan.Registrations.Add("    // approvals.AddLadder(\"credit-limit\", l => l");
                    plan.Registrations.Add("    //     .Rung(\"expert\", 1_000_000m, TimeSpan.FromHours(8))");
                    plan.Registrations.Add("    //     .Rung(\"manager\", 5_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)   // quorum is a rung property");
                    plan.Registrations.Add("    //     .TopRung(\"general-manager\", TimeSpan.FromHours(24)));");
                    plan.Registrations.Add("});");
                    plan.Endpoints.Add($"app.MapGoldpathApprovalsAdmin({(app.AuthWired ? "" : "exposeUnsecured: true")});      // worklist + decide verbs through the ENGINE (four eyes holds)");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathApprovalModel();      // approvals + delegations + signatures (worklist survives restarts)");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  approvals: true");
                    plan.NextSteps.Add("declare ladders in AddGoldpathApprovals — the escalation sweep is already scheduled (AddGoldpathApprovalsJobs, five-minute cron)");
                    return plan;
                }

            case "fileexchange":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — file pick-up runs on Jobs, which lives in the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "fileExchange" };
                    plan.ApiPackages.Add("Goldpath.FileExchange");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    plan.Endpoints.Add($"app.MapGoldpathFileExchangeAdmin({(app.AuthWired ? "" : "exposeUnsecured: true")});   // read-only: rails, files, quarantine with reasons");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    // your pick-up job rides here (the transport is yours — SFTP/share/object store is composed, not shipped):");
                        plan.JobsOptionsLines.Add("    // jobs.AddJob<RegistryPickupJob>(j => { j.Cron = \"0 0 6 * * ?\"; j.Deadline = TimeSpan.FromMinutes(30); });");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add("    // your pick-up job rides here (the transport is yours — SFTP/share/object store is composed, not shipped):");
                        plan.Registrations.Add("    // jobs.AddJob<RegistryPickupJob>(j => { j.Cron = \"0 0 6 * * ?\"; j.Deadline = TimeSpan.FromMinutes(30); });");
                        plan.Registrations.Add("});");
                    }

                    plan.Registrations.Add($"builder.AddGoldpathFileExchange<WebApplicationBuilder, {app.DbContextName}>(files =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR rails here (goldpath never guesses a counterparty format):");
                    plan.Registrations.Add("    // files.AddRail<MyRow>(\"registry-daily\", r => r.Header(1)");
                    plan.Registrations.Add("    //     .ParseLine(MyRow.Parse).ValidateRow(x => x.IsValid ? null : \"reason\")");
                    plan.Registrations.Add("    //     .Handle((row, ct) => ApplyAsync(row, ct)));");
                    plan.Registrations.Add("});");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathFileExchangeModel();  // processed keys + quarantine + archive marks");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  fileExchange: true");
                    plan.NextSteps.Add("declare rails in AddGoldpathFileExchange, then write the pick-up job for your transport and hang it on the jobs block (IGoldpathJob — chunked, resumable, visible in the console)");
                    return plan;
                }

            case "archival":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — the archive store lives in the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "archival" };
                    plan.ApiPackages.Add("Goldpath.Archival");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    jobs.AddGoldpathArchivalJobs<" + app.DbContextName + ">();    // archive nightly, purge chained after it, verify weekly");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add($"    jobs.AddGoldpathArchivalJobs<{app.DbContextName}>();    // archive nightly, purge chained after it, verify weekly");
                        plan.Registrations.Add("});");
                    }
                    plan.Registrations.Add($"builder.AddGoldpathArchival<WebApplicationBuilder, {app.DbContextName}>(archival =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR lifecycles here (goldpath never guesses domain retention):");
                    plan.Registrations.Add("    // archival.AddArchive<Order>(a => a.Key(o => o.Id)");
                    plan.Registrations.Add("    //     .DueWhen(o => o.Status == OrderStatus.Confirmed, o => o.CreatedAt)");
                    plan.Registrations.Add("    //     .ArchiveAfter(TimeSpan.FromDays(365)).RetainFor(years: 10).DeleteHotRowsAfterArchive());");
                    plan.Registrations.Add("});");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.Endpoints.Add($"app.MapGoldpathArchivalAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});    // lifecycle verbs: retrieve/hold/erase/verify");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathArchiveModel();   // archive entries + chain state + holds + erasure evidence");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  archival: true");
                    plan.NextSteps.Add("declare lifecycles in AddGoldpathArchival: Graph + Key + DueWhen + ArchiveAfter + RetainFor per aggregate");
                    plan.NextSteps.Add("classified data in an archived graph needs the dataprotection feature — erasure redacts through its catalog (GP1401)");
                    plan.NextSteps.Add("put /goldpath/admin/* behind an ops-scoped policy before exposing beyond the cluster boundary");
                    return plan;
                }

            case "bulk":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — the bulk file store lives in the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "bulk" };
                    plan.ApiPackages.Add("Goldpath.Bulk");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    jobs.AddGoldpathBulkJobs<" + app.DbContextName + ">();        // validate + execute runs (upload verb fires validate immediately)");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add($"    jobs.AddGoldpathBulkJobs<{app.DbContextName}>();        // validate + execute runs (upload verb fires validate immediately)");
                        plan.Registrations.Add("});");
                    }

                    plan.Registrations.Add($"builder.AddGoldpathBulk<WebApplicationBuilder, {app.DbContextName}>(bulk =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR batch shapes here (goldpath never guesses domain intake):");
                    plan.Registrations.Add("    // bulk.AddBatch<OrderImportRow>(\"orders\", b => b.MaxRows(10_000)");
                    plan.Registrations.Add("    //     .RowKey(r => r.Reference)");
                    plan.Registrations.Add("    //     .Validate((row, ctx) => { /* ctx.Fail(field, message) — value-free */ }));");
                    plan.Registrations.Add("});");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.Endpoints.Add($"app.MapGoldpathBulkAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // intake verbs: upload/report/approve/reject");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathBulk();           // files + batches + rows + value-free report");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  bulk: true");
                    plan.NextSteps.Add("declare batch shapes in AddGoldpathBulk: MaxRows (mandatory, GP1501) + RowKey + Validate per file kind");
                    plan.NextSteps.Add("register a row handler per shape: IGoldpathBulkRowHandler<TRow> — no SaveChanges inside (GP1502), the chunk batches it");
                    plan.NextSteps.Add("put /goldpath/admin/* behind an ops-scoped policy before exposing beyond the cluster boundary");
                    return plan;
                }

            case "notification":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — the notification evidence store lives in the app database and needs its connection name.");
                    var plan = new RecipePlan { ManifestKey = "notification" };
                    plan.ApiPackages.Add("Goldpath.Notification");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    jobs.AddGoldpathNotificationJobs<" + app.DbContextName + ">();   // send (frequent) + body-retention (nightly)");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add($"    jobs.AddGoldpathNotificationJobs<{app.DbContextName}>();   // send (frequent) + body-retention (nightly)");
                        plan.Registrations.Add("});");
                    }

                    plan.Registrations.Add($"builder.AddGoldpathNotification<WebApplicationBuilder, {app.DbContextName}>(notification =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR templates here (code templates: PR-reviewed, hash-stamped — GP1602 wants a retention window):");
                    plan.Registrations.Add("    // notification.AddTemplate(\"order-confirmed\", t => t");
                    plan.Registrations.Add("    //     .Channel(\"email\", c => c.Subject(\"\", \"...\").Body(\"\", \"... {{Token}} ...\"))");
                    plan.Registrations.Add("    //     .DeleteBodyAfter(TimeSpan.FromDays(90)));");
                    plan.Registrations.Add("});");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.Endpoints.Add($"app.MapGoldpathNotificationAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});   // read-only evidence views (recipients masked)");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathNotification();   // evidence rows + attachments");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  notification: true");
                    plan.NextSteps.Add("declare templates in AddGoldpathNotification (code, per channel per culture; DeleteBodyAfter is GP1602's ask)");
                    plan.NextSteps.Add("request through IGoldpathNotifier with a UNIQUE dedupKey — direct SmtpClient is GP1601-flagged (evidence hole)");
                    plan.NextSteps.Add("configure the channel: Goldpath:Notification:Email { Host, Port, UseSsl, User, Password, From }");
                    return plan;
                }

            case "campaign":
                {
                    var connection = app.ConnectionName
                        ?? throw new CliFailureException("no GetConnectionString(...) found in the composition root — the campaign plan lives in the app database and needs its connection name.");
                    if (!app.MessagingWired)
                    {
                        // The schema's cross-field rule, said at CLI time: no broker, no campaign.
                        throw new CliFailureException(
                            "features.campaign REQUIRES a broker (campaign RFC D8) — no AddGoldpathMessaging(...) found in the composition root. The release path IS broker fan-out: wire messaging first (a broker resource + the AddGoldpathMessaging block), then re-run.");
                    }

                    var plan = new RecipePlan { ManifestKey = "campaign" };
                    plan.ApiPackages.Add("Goldpath.Campaign");
                    plan.ApiPackages.Add("Goldpath.Jobs");
                    if (app.JobsWired)
                    {
                        // ONE scheduler per app: compose into the existing AddGoldpathJobs block.
                        plan.JobsOptionsLines.Add("    jobs.AddGoldpathCampaignJobs<" + app.DbContextName + ">();       // pacer: the cron guarantees a LEADER exists; pacing is in-memory ticks");
                    }
                    else
                    {
                        plan.Registrations.Add($"builder.AddGoldpathJobs<WebApplicationBuilder, {app.DbContextName}>(jobs =>");
                        plan.Registrations.Add("{");
                        plan.Registrations.Add($"    jobs.ConnectionName = \"{connection}\";              // runs + schedules live in the app database");
                        if (app.DatabaseProvider == "sqlserver")
                        {
                            plan.Registrations.Add("    jobs.Provider = GoldpathJobStoreProvider.SqlServer;");
                        }

                        plan.Registrations.Add($"    jobs.AddGoldpathCampaignJobs<{app.DbContextName}>();       // pacer: the cron guarantees a LEADER exists; pacing is in-memory ticks");
                        plan.Registrations.Add("});");
                    }

                    plan.Registrations.Add($"builder.AddGoldpathCampaign<WebApplicationBuilder, {app.DbContextName}>(campaign =>");
                    plan.Registrations.Add("{");
                    plan.Registrations.Add("    // Declare YOUR campaign types here (code, PR-reviewed; operators create INSTANCES via the admin API):");
                    plan.Registrations.Add("    // campaign.AddCampaign<YourTarget>(\"your-campaign\", c => c");
                    plan.Registrations.Add("    //     .MaxTargets(1_000_000)                    // mandatory — GP1701");
                    plan.Registrations.Add("    //     .Targets((services, parameters) => /* keyset-ORDERED IAsyncEnumerable */)");
                    plan.Registrations.Add("    //     .DefaultPolicy(p => p with { Tps = 50, MaxInFlight = 1_000 }));");
                    plan.Registrations.Add("});");
                    plan.BusLines.Add($"    bus.AddGoldpathCampaignConsumers<{app.DbContextName}>();    // claim-before-execute item consumer + batching outcome sink");
                    plan.Endpoints.Add($"app.MapGoldpathJobsAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});        // run console API: trigger/pause/reschedule/audit");
                    plan.Endpoints.Add($"app.MapGoldpathCampaignAdmin<{app.DbContextName}>({(app.AuthWired ? "" : "exposeUnsecured: true")});       // audited verbs: create/pause/resume/abort/throttle");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathCampaign();       // campaigns + items (the 30M-row table) + verb audit");
                    plan.ModelCalls.Add("        modelBuilder.AddGoldpathJobs();           // run model + clustered Quartz store (same database)");
                    plan.ManifestLines.Add("  campaign: true");
                    plan.NextSteps.Add("declare campaign types in AddGoldpathCampaign: MaxTargets (mandatory, GP1701) + a keyset-ORDERED Targets stream + DefaultPolicy");
                    plan.NextSteps.Add("register an item handler per type: IGoldpathCampaignItemHandler<TTarget> — no SaveChanges inside (GP1702), outcomes ride the sink");
                    plan.NextSteps.Add("operators launch instances via POST /goldpath/admin/campaign (audited); throttle is LIVE — no restart to slow a screaming gateway");
                    plan.NextSteps.Add("put /goldpath/admin/* behind an ops-scoped policy before exposing beyond the cluster boundary");
                    return plan;
                }

            default:
                throw new CliUsageException($"unknown feature '{feature}' — one of: {string.Join(", ", Names)}");
        }
    }
}
