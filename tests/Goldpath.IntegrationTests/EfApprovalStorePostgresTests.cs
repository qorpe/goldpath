using Goldpath.Approvals.Tests;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>One PostgreSQL container for the twin below; each fact gets a database of its own.</summary>
public sealed class ApprovalStorePostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int _databases;

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"gp_appr_{Interlocked.Increment(ref _databases)}";
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>
/// <see cref="EfApprovalStoreFacts"/> on PostgreSQL (#480). The facts ran on SQLite and Oracle only; the store's two
/// concurrency rules — the unique (RequestId, SignedBy) key and the request's version — are the database's to hold,
/// so they run on the database the concurrent-decision proof (<see cref="ApprovalConcurrencyTests"/>) uses as well.
/// </summary>
public sealed class EfApprovalStorePostgresTests(ApprovalStorePostgresFixture postgres)
    : EfApprovalStoreFacts, IClassFixture<ApprovalStorePostgresFixture>
{
    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        return b => b.UseNpgsql(connection);
    }
}
