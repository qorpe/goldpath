using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Goldpath.Approvals.Tests;

/// <summary><see cref="EfApprovalStoreFacts"/> on SQLite, in memory: one connection per test, open for its whole life.</summary>
public sealed class EfApprovalStoreTests : EfApprovalStoreFacts
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
    {
        await _connection.OpenAsync();
        return b => b.UseSqlite(_connection);
    }

    protected override Task CloseDatabaseAsync() => _connection.DisposeAsync().AsTask();
}
