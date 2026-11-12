using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Testcontainers.Oracle;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// One Oracle Database Free container per xUnit collection (G7, slice O-1, #404; RFC goldpath-oracle §4, §8). The
/// image is pinned by its full tag: the floating <c>23</c>, <c>slim</c> and <c>latest</c> are forbidden. Tests that
/// need it are <c>Category=Oracle</c> and run in CI's own <c>oracle</c> job, never in the <c>integration</c> job.
/// </summary>
public sealed class OracleFixture : IAsyncLifetime
{
    /// <summary>The one place the Oracle image is named.</summary>
    public const string Image = "gvenzl/oracle-free:23.26.3-slim-faststart";

    private readonly OracleContainer _oracle = new OracleBuilder(Image).Build();

    /// <summary>How long the container took to accept connections; ORA-2 (#405) records the number.</summary>
    public TimeSpan StartUpTime { get; private set; }

    /// <summary>An ODP.NET connection string to the container's application database.</summary>
    public string ConnectionString => _oracle.GetConnectionString();

    public async Task InitializeAsync()
    {
        var started = Stopwatch.StartNew();
        await _oracle.StartAsync();
        StartUpTime = started.Elapsed;
    }

    public Task DisposeAsync() => _oracle.DisposeAsync().AsTask();

    /// <summary>
    /// The most pooled sessions one schema's connection string may hold (#571). Oracle Free runs with
    /// <c>processes=200</c>, of which about 85 are the database's own background processes, and every class in the
    /// collection shares this one container. Measured in the lane on 2026-11-12: the campaign suite's host alone held 90
    /// sessions while suites that had already finished still held 15, and a consumer that could not get a session after
    /// its claim left the item Processing until the ten-minute stale-claim sweep. Bounded, one suite cannot take the
    /// container's last sessions; a burst past the bound waits for a pooled session instead of failing on ORA-12516.
    /// </summary>
    public const int MaxPoolSize = 48;

    /// <summary>
    /// A fresh, empty schema (an Oracle user) on the container, created as SYSTEM, so a test that needs an empty
    /// database does not depend on another test's order. Returns its ODP.NET connection string, its pool bounded by
    /// <see cref="MaxPoolSize"/>. Creating one first releases every idle pooled session the process holds: the classes
    /// before it in the collection have finished (it runs one class at a time), and their idle sessions would otherwise
    /// count against the container's limit until the pool's own idle timer closed them (#571).
    /// </summary>
    public async Task<string> CreateSchemaAsync(string name)
    {
        OracleConnection.ClearAllPools();
        var app = new OracleConnectionStringBuilder(ConnectionString);
        var system = new OracleConnectionStringBuilder(ConnectionString) { UserID = "system" };
        await using (var connection = new OracleConnection(system.ConnectionString))
        {
            await connection.OpenAsync();
            foreach (var statement in new[]
            {
                $"CREATE USER {name} IDENTIFIED BY \"{app.Password}\" QUOTA UNLIMITED ON USERS",
                $"GRANT CREATE SESSION, CREATE TABLE, CREATE SEQUENCE, CREATE VIEW, CREATE PROCEDURE, CREATE TRIGGER TO {name}",
            })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement;
                await command.ExecuteNonQueryAsync();
            }
        }

        return new OracleConnectionStringBuilder(ConnectionString) { UserID = name, MaxPoolSize = MaxPoolSize }.ConnectionString;
    }
}

/// <summary>
/// The collection every Oracle-backed test class joins, so they share one container. It runs with parallelization
/// DISABLED — after every parallel collection, alone — because its Quartz-hosting twins (Campaign, Bulk, Archival,
/// Notification on Oracle) share Quartz's process-global state with the PostgreSQL suites in the
/// <c>quartz-process-globals</c> collection: run side by side in one process, a PostgreSQL scheduler was handed the
/// Oracle data source (<c>Unable to cast … OracleConnection to … NpgsqlConnection</c>, #409). CI's lanes never mix
/// the two, so this costs the <c>oracle</c> lane nothing it was not already doing one class at a time; it makes a
/// whole-project run, or one filter naming both suites, honest.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OracleCollection : ICollectionFixture<OracleFixture>
{
    /// <summary>The collection's name.</summary>
    public const string Name = "oracle";
}

/// <summary>The lane's first proof: the pinned image starts, and ODP.NET talks to it.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class OracleContainerTests(OracleFixture oracle, ITestOutputHelper output)
{
    [Fact]
    public async Task The_pinned_image_starts_and_answers_odp_net()
    {
        output.WriteLine($"oracle: {OracleFixture.Image} accepted connections after {oracle.StartUpTime.TotalSeconds:F1} s");

        await using var connection = new OracleConnection(oracle.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM DUAL";
        Assert.Equal(1m, Convert.ToDecimal(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));

        output.WriteLine($"oracle: server version {connection.ServerVersion}");
        var version = Version.Parse(string.Join('.', connection.ServerVersion.Split('.').Take(2)));
        Assert.True(version >= new Version(23, 26), $"the server reports {connection.ServerVersion}; the lane runs 23.26 or later");
    }
}
