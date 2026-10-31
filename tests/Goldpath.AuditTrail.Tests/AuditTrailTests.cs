using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Goldpath.Tests;

/// <summary><see cref="AuditTrailFacts"/> on SQLite, in memory: each database is a connection held open for the test's life.</summary>
public sealed class AuditTrailTests : AuditTrailFacts
{
    private readonly List<SqliteConnection> _connections = [];

    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        _connections.Add(connection);
        await connection.OpenAsync();
        return o => o.UseSqlite(connection);
    }

    protected override async Task CloseDatabasesAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }
    }
}
