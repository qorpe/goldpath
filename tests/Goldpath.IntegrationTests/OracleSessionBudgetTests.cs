using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The Oracle collection's session budget (#571). One Oracle Free container serves every Oracle class, and it runs with
/// <c>processes=200</c>. A class that has finished must not keep its pooled sessions open while the next one runs, and
/// one class's pool must be bounded; otherwise the campaign suite's consumers met ORA-12516 after their claims, about
/// half the time, and the campaign hung on items left Processing.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class OracleSessionBudgetTests(OracleFixture oracle)
{
    [Fact]
    public async Task Creating_the_next_schema_releases_the_idle_sessions_an_earlier_class_left_in_its_pool()
    {
        var earlier = await oracle.CreateSchemaAsync("GP_BUDGET_A");
        var connections = new List<OracleConnection>();
        for (var i = 0; i < 6; i++)
        {
            var connection = new OracleConnection(earlier);
            await connection.OpenAsync();
            connections.Add(connection);
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync();   // back to the pool, idle, and still a session on the server
        }

        // ODP.NET grows a pool in increments (Incr Pool Size, 5 by default), so it may hold more than six.
        var idle = await SessionsOfAsync("GP_BUDGET_A");
        Assert.True(idle >= 6, $"the earlier schema holds {idle} sessions; at least the six it opened were expected");

        await oracle.CreateSchemaAsync("GP_BUDGET_B");

        Assert.Equal(0, await SessionsOfAsync("GP_BUDGET_A"));
    }

    [Fact]
    public async Task A_schemas_pool_is_bounded()
    {
        var schema = await oracle.CreateSchemaAsync("GP_BUDGET_C");

        Assert.Equal(OracleFixture.MaxPoolSize, new OracleConnectionStringBuilder(schema).MaxPoolSize);
        Assert.InRange(OracleFixture.MaxPoolSize, 1, 64);   // 64 + the database's own ~85 processes stays well under 200
    }

    /// <summary>The server's count of <paramref name="user"/>'s sessions, read as SYSTEM on a connection outside every pool.</summary>
    private async Task<int> SessionsOfAsync(string user)
    {
        var system = new OracleConnectionStringBuilder(oracle.ConnectionString) { UserID = "system", Pooling = false };
        await using var connection = new OracleConnection(system.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM v$session WHERE username = :name";
        command.Parameters.Add(new OracleParameter("name", user));
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
