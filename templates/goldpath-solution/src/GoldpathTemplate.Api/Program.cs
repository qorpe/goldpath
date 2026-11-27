#if (UseCampaign && !UseBroker)
#error features.campaign REQUIRES a broker (campaign RFC D8): the release path IS broker fan-out. Regenerate with --broker rabbitmq (or kafka).
#endif
#if (UseBroker)
using MassTransit;
#endif
using Mediant.AspNetCore.Mapping;
using Mediant.DependencyInjection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddGoldpathServiceDefaults();
builder.AddGoldpathApiDefaults();
//#if (UseApiKey)
builder.AddGoldpathAuth(o => o.Strategy = GoldpathAuthStrategy.ApiKey);
//#elif (UseAuth)
builder.AddGoldpathAuth();   // OpenId: set Goldpath:Auth:Authority AND Audience in configuration (authority without audience refuses to start — A3)
//#endif
// goldpath:features registrations — the drift profile is the source of these rows
//#if (UseMultiTenancy)
builder.AddGoldpathMultiTenancy();
//#endif
//#if (UseAuditTrail)
builder.AddGoldpathAuditTrail<WebApplicationBuilder, OrdersDbContext>();
//#endif
//#if (UseSoftDelete)
builder.AddGoldpathSoftDelete();
//#endif
//#if (UseCaching)
builder.AddGoldpathCaching();                       // HybridCache L1+L2 (redis resource in the AppHost)
//#endif
//#if (UseIdempotency && !UseCaching)
builder.Services.AddDistributedMemoryCache();  // idempotency store fallback; enable caching for Redis-backed keys
//#endif
//#if (UseIdempotency)
builder.AddGoldpathIdempotency();
//#endif
//#if (UseDataProtection)
builder.AddGoldpathDataProtection();
//#endif
//#if (UseApprovals)
builder.AddGoldpathApprovals<WebApplicationBuilder, OrdersDbContext>(approvals =>
{
    // Declare YOUR authority chains here (goldpath never guesses who may approve):
    // approvals.AddLadder("credit-limit", l => l
    //     .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
    //     .Rung("manager", 5_000_000m, TimeSpan.FromHours(8), requiredApprovals: 2)   // quorum is a rung property
    //     .TopRung("general-manager", TimeSpan.FromHours(24)));
});
//#endif
//#if (UseFileExchange)
builder.AddGoldpathFileExchange<WebApplicationBuilder, OrdersDbContext>(files =>
{
    // Declare YOUR rails here (goldpath never guesses a counterparty format):
    // files.AddRail<MyRow>("registry-daily", r => r.Header(1)
    //     .ParseLine(MyRow.Parse).ValidateRow(x => x.IsValid ? null : "reason")
    //     .Handle((row, ct) => ApplyAsync(row, ct)));
});
//#endif
//#if (UseLocking && UsePostgres)
builder.AddGoldpathLocking(o =>
{
    o.Provider = GoldpathLockProvider.Postgres;     // the lock lives in the app database — zero new infra
    o.ConnectionName = "ordersdb";
});
//#endif
//#if (UseLocking && UseSqlServer)
builder.AddGoldpathSqlServerLocking(o => o.ConnectionName = "ordersdb");
//#endif
//#if (UseLocking && UseOracle)
builder.AddGoldpathOracleLocking(o => o.ConnectionName = "ordersdb");   // DBMS_LOCK: the runtime user needs EXECUTE ON SYS.DBMS_LOCK (Goldpath.Locking.Oracle README)
//#endif
//#if (UseArchival || UseBulk || UseNotification || UseCampaign || UseApprovals || UseFileExchange)
builder.AddGoldpathJobs<WebApplicationBuilder, OrdersDbContext>(jobs =>
{
    jobs.ConnectionName = "ordersdb";              // runs + schedules live in the app database
//#if (UseSqlServer)
    jobs.Provider = GoldpathJobStoreProvider.SqlServer;
//#endif
//#if (UseOracle)
    jobs.Provider = GoldpathJobStoreProvider.Oracle;
//#endif
//#if (UseArchival)
    jobs.AddGoldpathArchivalJobs<OrdersDbContext>();    // archive nightly, purge chained after it, verify weekly
//#endif
//#if (UseBulk)
    jobs.AddGoldpathBulkJobs<OrdersDbContext>();        // validate + execute runs (upload verb fires validate immediately)
//#endif
//#if (UseNotification)
    jobs.AddGoldpathNotificationJobs<OrdersDbContext>();   // send (frequent) + body-retention (nightly)
//#endif
//#if (UseCampaign)
    jobs.AddGoldpathCampaignJobs<OrdersDbContext>();       // pacer: the cron guarantees a LEADER exists; pacing is in-memory ticks
//#endif
//#if (UseApprovals)
    jobs.AddGoldpathApprovalsJobs();               // escalation sweep — overdue rungs move up, the top rung expires
//#endif
//#if (UseFileExchange)
    // your pick-up job rides here (the transport is yours — SFTP/share/object store is composed, not shipped):
    // jobs.AddJob<RegistryPickupJob>(j => { j.Cron = "0 0 6 * * ?"; j.Deadline = TimeSpan.FromMinutes(30); });
//#endif
});
//#endif
//#if (UseArchival)
builder.AddGoldpathArchival<WebApplicationBuilder, OrdersDbContext>(archival =>
{
    // The walking-skeleton lifecycle: confirmed orders archive after a year, retained ten.
    archival.AddArchive<Order>(a => a
        .Key(o => o.Id)
        .DueWhen(o => o.Status == OrderStatus.Confirmed, o => o.CreatedAt)
        .ArchiveAfter(TimeSpan.FromDays(365))
        .RetainFor(years: 10)
        .DeleteHotRowsAfterArchive());
});
//#endif
//#if (UseBulk)
builder.AddGoldpathBulk<WebApplicationBuilder, OrdersDbContext>(bulk =>
{
    // The walking-skeleton intake: an order-import file — validated, gated, executed as a run.
    bulk.AddBatch<OrderImportRow>("orders", b => b
        .MaxRows(10_000)
        .RowKey(r => r.Reference)
        .Validate((row, ctx) =>
        {
            if (row.Amount <= 0)
            {
                ctx.Fail(nameof(row.Amount), "amount must be positive");
            }
        }));
});
builder.Services.AddScoped<IGoldpathBulkRowHandler<OrderImportRow>, OrderImportHandler>();
//#endif
//#if (UseNotification)
builder.AddGoldpathNotification<WebApplicationBuilder, OrdersDbContext>(notification =>
{
    // The walking-skeleton notice: order confirmed -> one evidence-backed email.
    // Config: Goldpath:Notification:Email { Host, Port, UseSsl, User, Password, From }.
    notification.AddTemplate(OrderNotifications.OrderConfirmedTemplate, t => t
        .Channel("email", c => c
            .Subject("", "Your order {{Reference}} is confirmed")
            .Body("", "Dear customer, your order {{Reference}} ({{Amount}}) is confirmed."))
        .DeleteBodyAfter(TimeSpan.FromDays(90)));      // evidence survives, content goes
});
//#endif
//#if (UseCampaign)
builder.AddGoldpathCampaign<WebApplicationBuilder, OrdersDbContext>(campaign =>
{
    // The walking-skeleton campaign: win back customers who went quiet for a year.
    // Operators CREATE instances over this type through /goldpath/admin/campaign (audited).
    campaign.AddCampaign<DormantCustomerTarget>(OrderCampaigns.WinbackType, c => c
        .MaxTargets(1_000_000)                         // the ceiling is a decision, not a default (GP1701)
        .Targets((services, parameters) => services.GetRequiredService<OrdersDbContext>()
            .Orders.AsNoTracking()
            .Where(o => o.CreatedAt < DateTimeOffset.UtcNow.AddDays(-int.Parse(parameters["quietDays"])))
            .OrderBy(o => o.Id)                        // stable order: the watermark resumes BY COUNT
            .Select(o => new DormantCustomerTarget(o.Id, o.Reference))
            .AsAsyncEnumerable())
        .DefaultPolicy(p => p with { Tps = 50, MaxInFlight = 1_000 }));
});
builder.Services.AddScoped<IGoldpathCampaignItemHandler<DormantCustomerTarget>, WinbackHandler>();
//#endif

//#if (UseCleanArch)
builder.Services.AddMediant(cfg => cfg.RegisterServicesFromAssembly(typeof(CreateOrderCommand).Assembly));   // handlers live in Application
//#else
builder.Services.AddMediant(cfg => cfg.RegisterServicesFromAssembly(typeof(Program).Assembly));
//#endif

// Connection strings come from the AppHost; under the build-time OpenAPI generator they
// are absent — configuration stays tolerant, usage would fail loudly anyway.
var ordersDbConnection = builder.Configuration.GetConnectionString("ordersdb");
builder.AddGoldpathData<WebApplicationBuilder, OrdersDbContext>(options =>
{
    // No connection (docgen, `dotnet ef` design time): the PROVIDER still binds — the
    // model needs it to exist; nothing connects until a real string is used.
#if (UsePostgres)
    if (ordersDbConnection is not null)
    {
        options.UseNpgsql(ordersDbConnection);
    }
    else
    {
        options.UseNpgsql();
    }
#endif
#if (UseSqlServer)
    if (ordersDbConnection is not null)
    {
        options.UseSqlServer(ordersDbConnection);
    }
    else
    {
        options.UseSqlServer();
    }
#endif
#if (UseOracle)
    if (ordersDbConnection is not null)
    {
        options.UseOracle(ordersDbConnection);
    }
    else
    {
        options.UseOracle();
    }
#endif
});
//#if (UseCleanArch)
builder.Services.AddScoped<IOrdersDbContext>(sp => sp.GetRequiredService<OrdersDbContext>());   // the Application seam resolves to the real context
//#endif

#if (UseBroker)
builder.AddGoldpathMessaging(bus =>
{
    // goldpath:features consumers — bus-riding features register here
    bus.AddGoldpathHandler<OrderPlaced, OrderPlacedHandler>();   // the consume seam (ADR-0013): no bus type in the handler
//#if (UseCampaign)
    bus.AddGoldpathCampaignConsumers<OrdersDbContext>();    // claim-before-execute item consumer + batching outcome sink
//#endif
    bus.AddGoldpathOutbox<OrdersDbContext>(outbox =>
    {
#if (UsePostgres)
        outbox.UsePostgres();
#endif
#if (UseSqlServer)
        outbox.UseSqlServer();
#endif
    });
    bus.UsingRabbitMq((context, cfg) =>
    {
        if (builder.Configuration.GetConnectionString("messaging") is { } messagingConnection)
        {
            cfg.Host(new Uri(messagingConnection));
        }

        cfg.ConfigureGoldpathEndpoints(context);
    });
});
#endif

var app = builder.Build();

// goldpath:features middleware — the drift profile is the source of these rows
//#if (UseMultiTenancy)
app.UseGoldpathMultiTenancy();                      // resolve the tenant BEFORE auth binds to it
//#endif
//#if (UseAuth)
app.UseGoldpathAuth();
//#endif
app.MapGoldpathDefaultEndpoints();
//#if (UseCleanArch)
app.MapMediantEndpoints(typeof(CreateOrderCommand).Assembly);   // the endpoints ride the Application assembly's commands
//#else
app.MapMediantEndpoints(typeof(Program).Assembly);
//#endif
// goldpath:features endpoints — admin surfaces map here (put them behind the auth floor)
//#if (UseArchival || UseBulk || UseNotification || UseCampaign || UseApprovals || UseFileExchange)
#if (UseAuth)
app.MapGoldpathJobsAdmin<OrdersDbContext>();        // run console API: trigger/pause/reschedule/audit — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathJobsAdmin<OrdersDbContext>(exposeUnsecured: true);        // run console API: trigger/pause/reschedule/audit
#endif
//#endif
//#if (UseArchival)
#if (UseAuth)
app.MapGoldpathArchivalAdmin<OrdersDbContext>();    // lifecycle verbs: retrieve/hold/erase/verify — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathArchivalAdmin<OrdersDbContext>(exposeUnsecured: true);    // lifecycle verbs: retrieve/hold/erase/verify
#endif
//#endif
//#if (UseBulk)
#if (UseAuth)
app.MapGoldpathBulkAdmin<OrdersDbContext>();        // intake verbs: upload/report/approve/reject — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathBulkAdmin<OrdersDbContext>(exposeUnsecured: true);        // intake verbs: upload/report/approve/reject
#endif
//#endif
//#if (UseNotification)
#if (UseAuth)
app.MapGoldpathNotificationAdmin<OrdersDbContext>();   // read-only evidence views (recipients masked) — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathNotificationAdmin<OrdersDbContext>(exposeUnsecured: true);   // read-only evidence views (recipients masked)
#endif
//#endif
//#if (UseCampaign)
#if (UseAuth)
app.MapGoldpathCampaignAdmin<OrdersDbContext>();       // audited verbs: create/pause/resume/abort/throttle — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathCampaignAdmin<OrdersDbContext>(exposeUnsecured: true);       // audited verbs: create/pause/resume/abort/throttle
#endif
//#endif
//#if (UseApprovals)
#if (UseAuth)
app.MapGoldpathApprovalsAdmin();                       // decide verbs through the ENGINE (four eyes holds) — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathApprovalsAdmin(exposeUnsecured: true);                       // decide verbs through the ENGINE (four eyes holds)
#endif
//#endif
//#if (UseFileExchange)
#if (UseAuth)
app.MapGoldpathFileExchangeAdmin();                    // read-only: rails, files, quarantine with reasons — ops policy REQUIRED (H2)
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathFileExchangeAdmin(exposeUnsecured: true);                    // read-only: rails, files, quarantine with reasons
#endif
//#endif

//#if (UseArchival || UseBulk || UseNotification || UseCampaign || UseApprovals || UseFileExchange)
// The console over those surfaces — served by THIS head from the package's embedded
// assets (no Node in this solution, by construction). One line to remove if your ops team
// drives the API directly; it adds no capability the surfaces above do not already expose.
#if (UseAuth)
app.MapGoldpathConsole();                           // behind the SAME ops floor as the surfaces
#else
// No auth strategy in this shape: the opt-out is WRITTEN HERE so the decision stays
// visible — acceptable only behind an authenticating boundary (mTLS/gateway).
app.MapGoldpathConsole(exposeUnsecured: true);
#endif
//#endif

// Skip schema work under the build-time OpenAPI generator (no database there).
var isDocGen = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
if (app.Environment.IsDevelopment() && !isDocGen)
{
    // Development only (GP0302): real environments apply the CI migration bundle —
    // and Development walks the SAME migrations (EnsureCreated writes no history row;
    // a database born from it can never take a migration later — migrations RFC D2).
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

    // SERIALIZED across replicas: two instances booting together race MigrateAsync into
    // "relation already exists" (found live in the api-portal product's HA exam, 2026-08).
    // A database-side application lock lets the first replica migrate while the second
    // waits and then walks an already-applied history. Single-replica pays one round trip.
#if (UsePostgres)
    await db.Database.OpenConnectionAsync();
    try
    {
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(727401001)");
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(727401001)");
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }
#endif
#if (UseSqlServer)
    // On SqlServer the app database does not exist until MigrateAsync creates it, so the
    // lock is taken on master — a connection to the APP database here would spin on
    // "database does not exist" retries until the health deadline (found generating this
    // very shape). Session-owned: the lock dies with this connection, crash included.
    var gateBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
        db.Database.GetConnectionString()!) { InitialCatalog = "master" };
    await using (var gate = new Microsoft.Data.SqlClient.SqlConnection(gateBuilder.ConnectionString))
    {
        await gate.OpenAsync();
        await using (var acquire = gate.CreateCommand())
        {
            acquire.CommandText = "EXEC sp_getapplock @Resource = 'goldpath-dev-migrate', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 300000";
            await acquire.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();

        await using (var release = gate.CreateCommand())
        {
            release.CommandText = "EXEC sp_releaseapplock @Resource = 'goldpath-dev-migrate', @LockOwner = 'Session'";
            await release.ExecuteNonQueryAsync();
        }
    }
#endif
#if (UseOracle)
    // On Oracle every DDL statement commits, so a lock held by the MIGRATING connection would be
    // released by its first CREATE TABLE. The gate is a table lock held in a transaction on a
    // SECOND connection instead. It needs no grant beyond CREATE TABLE (DBMS_LOCK needs EXECUTE
    // on SYS.DBMS_LOCK, which Oracle does not grant to PUBLIC), and it is session-owned: it dies
    // with this connection, crash included. The first replica creates the gate table; a replica
    // racing it gets ORA-00955 (the name is taken), which is the table existing, not a failure.
    await using (var gate = new Oracle.ManagedDataAccess.Client.OracleConnection(db.Database.GetConnectionString()))
    {
        await gate.OpenAsync();
        await using (var create = gate.CreateCommand())
        {
            create.CommandText = "BEGIN EXECUTE IMMEDIATE 'CREATE TABLE GOLDPATH_DEV_MIGRATE_GATE (ID NUMBER(1))'; EXCEPTION WHEN OTHERS THEN IF SQLCODE != -955 THEN RAISE; END IF; END;";
            await create.ExecuteNonQueryAsync();
        }

        using var held = gate.BeginTransaction();
        await using (var acquire = gate.CreateCommand())
        {
            acquire.Transaction = held;
            acquire.CommandText = "LOCK TABLE GOLDPATH_DEV_MIGRATE_GATE IN EXCLUSIVE MODE WAIT 300";
            await acquire.ExecuteNonQueryAsync();
        }

        await db.Database.MigrateAsync();
        held.Rollback();   // ends the transaction, and with it the lock
    }
#endif
}

app.Run();
