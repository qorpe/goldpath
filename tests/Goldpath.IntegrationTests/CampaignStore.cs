using Microsoft.EntityFrameworkCore;
using Oracle.EntityFrameworkCore.Infrastructure;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.PostgreSql;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The database a Campaign suite runs on (G7 ORA-5, #408). The suites are written once: PostgreSQL in the
/// <c>integration</c> lane (a container per test, as before), Oracle in the <c>oracle</c> lane (a fresh schema per test
/// on the collection's one container, with the clustered Quartz store on Oracle, ORA-3 #406).
/// </summary>
public sealed class CampaignStore
{
    private static int _schemas;

    private readonly Func<Task<string>> _start;
    private readonly Func<Task> _stop;
    private readonly Action<DbContextOptionsBuilder, string> _use;
    private string? _connection;

    private CampaignStore(Func<Task<string>> start, Func<Task> stop, Action<DbContextOptionsBuilder, string> use, GoldpathJobStoreProvider jobs)
    {
        _start = start;
        _stop = stop;
        _use = use;
        JobsProvider = jobs;
    }

    /// <summary>The Quartz store provider the suite's hosts configure.</summary>
    public GoldpathJobStoreProvider JobsProvider { get; }

    /// <summary>True on the Oracle lane.</summary>
    public bool IsOracle => JobsProvider == GoldpathJobStoreProvider.Oracle;

    /// <summary>The started store's connection string.</summary>
    public string ConnectionString => _connection ?? throw new InvalidOperationException("the store is not started");

    /// <summary>A PostgreSQL 17 container of the suite's own.</summary>
    public static CampaignStore PostgreSql()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        return new CampaignStore(
            async () =>
            {
                await container.StartAsync();
                return container.GetConnectionString();
            },
            () => container.DisposeAsync().AsTask(),
            (options, connection) => options.UseNpgsql(connection),
            GoldpathJobStoreProvider.Postgres);
    }

    /// <summary>A fresh schema on the Oracle collection's container, in the 19c SQL form the RFC ships.</summary>
    public static CampaignStore Oracle(OracleFixture oracle)
    {
        string? connection = null;
        return new(
            async () => connection = await oracle.CreateSchemaAsync($"GP_CAMP_{Interlocked.Increment(ref _schemas)}"),
            () =>
            {
                // The container is the collection's, so it outlives the suite; its pooled sessions must not. Oracle Free's
                // session limit is reached after a handful of suites otherwise (ORA-12516, measured).
                if (connection is not null)
                {
                    using var pooled = new OracleConnection(connection);
                    OracleConnection.ClearPool(pooled);
                }

                return Task.CompletedTask;
            },
            (options, schema) => options.UseOracle(schema, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19)),
            GoldpathJobStoreProvider.Oracle);
    }

    /// <summary>Starts the store and creates the suite's schema.</summary>
    public async Task StartAsync()
    {
        _connection = await _start();
        await using var db = new CampaignTests.CampDb(Options<CampaignTests.CampDb>());
        await db.Database.EnsureCreatedAsync();
    }

    /// <summary>Releases what <see cref="StartAsync"/> took.</summary>
    public Task StopAsync() => _stop();

    /// <summary>Points a context at the store.</summary>
    public void Use(DbContextOptionsBuilder options) => _use(options, ConnectionString);

    /// <summary>A context's options on the store.</summary>
    public DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        Use(options);
        return options.Options;
    }
}
