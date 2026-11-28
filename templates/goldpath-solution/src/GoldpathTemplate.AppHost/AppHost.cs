var builder = DistributedApplication.CreateBuilder(args);

#if (UsePostgres)
var database = builder.AddPostgres("dbserver").AddDatabase("ordersdb");
#endif
#if (UseSqlServer)
var database = builder.AddSqlServer("dbserver").AddDatabase("ordersdb");
#endif
#if (UseOracle)
// The image is PINNED (Oracle RFC §4: never `23`, `slim` or `latest`), and it is the image every
// Oracle proof in Goldpath runs on. It reads its password from ORACLE_PASSWORD; Aspire sets
// ORACLE_PWD for Oracle's own image, so the same parameter is handed to both names. The app
// database is the image's pluggable database, FREEPDB1, which exists from the first boot.
var dbServer = builder.AddOracle("dbserver")
    .WithImageRegistry("docker.io")
    .WithImage("gvenzl/oracle-free", "23.26.3-slim-faststart");
dbServer.WithEnvironment(context => context.EnvironmentVariables["ORACLE_PASSWORD"] = dbServer.Resource.PasswordParameter);
var database = dbServer.AddDatabase("ordersdb", "FREEPDB1");
#endif
#if (UseBroker)
var messaging = builder.AddRabbitMQ("messaging");
#endif
// goldpath:features resources — the drift profile is the source of these rows
#if (UseCaching)
var cache = builder.AddRedis("redis");
#endif

builder.AddProject<Projects.GoldpathTplSafe_Api>("api")
    .WithReference(database).WaitFor(database)
#if (UseBroker)
    .WithReference(messaging).WaitFor(messaging)
#endif
    // goldpath:features references — the drift profile is the source of these rows
#if (UseCaching)
    .WithReference(cache).WaitFor(cache)
#endif
    .WithHttpHealthCheck("/health/ready");

// goldpath:workers — additional worker projects wire here (goldpath add worker)

builder.Build().Run();
