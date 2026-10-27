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
}

/// <summary>The collection every Oracle-backed test class joins, so they share one container.</summary>
[CollectionDefinition(Name)]
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
