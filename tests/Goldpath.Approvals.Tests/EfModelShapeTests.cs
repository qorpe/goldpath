using Goldpath;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The mapped model is a CONTRACT: table and column shapes are what an adopter's migration
/// freezes, and what a raw psql session reads years later. These facts pin every name,
/// length and conversion the mapping declares.
/// </summary>
public sealed class EfModelShapeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public EfModelShapeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _provider = new ServiceCollection()
            .AddDbContext<EfApprovalStoreTests.ApprovalsDbContext>(b => b.UseSqlite(_connection))
            .BuildServiceProvider(true);
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<EfApprovalStoreTests.ApprovalsDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Theory]
    [InlineData(typeof(GoldpathApprovalRequest), "GoldpathApprovals")]
    [InlineData(typeof(GoldpathApprovalDelegationRow), "GoldpathApprovalDelegations")]
    [InlineData(typeof(GoldpathApprovalSignatureRow), "GoldpathApprovalSignatures")]
    public void The_three_tables_keep_their_declared_names(Type entity, string table)
    {
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<EfApprovalStoreTests.ApprovalsDbContext>().Model;
        Assert.Equal(table, model.FindEntityType(entity)!.GetTableName());
    }

    [Theory]
    [InlineData(typeof(GoldpathApprovalRequest), "Ladder", 128)]
    [InlineData(typeof(GoldpathApprovalRequest), "Subject", 256)]
    [InlineData(typeof(GoldpathApprovalRequest), "RequestedBy", 128)]
    [InlineData(typeof(GoldpathApprovalRequest), "PendingRole", 128)]
    [InlineData(typeof(GoldpathApprovalRequest), "DecidedBy", 128)]
    [InlineData(typeof(GoldpathApprovalRequest), "Status", 16)]
    [InlineData(typeof(GoldpathApprovalDelegationRow), "From", 128)]
    [InlineData(typeof(GoldpathApprovalDelegationRow), "To", 128)]
    [InlineData(typeof(GoldpathApprovalSignatureRow), "SignedBy", 128)]
    [InlineData(typeof(GoldpathApprovalSignatureRow), "Role", 128)]
    public void Column_lengths_stay_as_the_migration_froze_them(Type entity, string property, int maxLength)
    {
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<EfApprovalStoreTests.ApprovalsDbContext>().Model;
        Assert.Equal(maxLength, model.FindEntityType(entity)!.FindProperty(property)!.GetMaxLength());
    }

    [Theory]
    [InlineData(typeof(GoldpathApprovalRequest), "Status", false)]
    [InlineData(typeof(GoldpathApprovalDelegationRow), "Until", false)]
    // #480: one identity signs a request once, held by the database. It leads with RequestId, so it also serves the
    // per-request lookup the single-column RequestId index it replaced served.
    [InlineData(typeof(GoldpathApprovalSignatureRow), "RequestId,SignedBy", true)]
    public void The_declared_indexes_exist(Type entity, string properties, bool unique)
    {
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<EfApprovalStoreTests.ApprovalsDbContext>().Model;
        Assert.Contains(model.FindEntityType(entity)!.GetIndexes(),
            i => string.Join(",", i.Properties.Select(p => p.Name)) == properties && i.IsUnique == unique);
    }

    [Fact]
    public void The_request_row_carries_a_concurrency_token_and_nothing_else_does()
    {
        // #480: a write from a stale read is refused by the version the request was read at.
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<EfApprovalStoreTests.ApprovalsDbContext>().Model;
        var tokens = model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => $"{e.GetTableName()}.{p.GetColumnName()}:{p.ClrType.Name}"));
        Assert.Equal(["GoldpathApprovals.Version:Int64"], tokens);
    }

    [Fact]
    public async Task Status_is_stored_as_a_readable_string_not_an_int()
    {
        var store = new GoldpathEfApprovalStore<EfApprovalStoreTests.ApprovalsDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>());
        var request = new GoldpathApprovalRequest
        {
            Id = Guid.NewGuid(),
            Ladder = "credit-limit",
            Subject = "K26-200",
            RequestedBy = "maker",
            PendingRole = "expert",
            Status = GoldpathApprovalStatus.Withdrawn,
        };
        await store.AddAsync(request);

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT Status FROM GoldpathApprovals WHERE Id = $id";
        command.Parameters.AddWithValue("$id", request.Id);
        Assert.Equal("Withdrawn", (string?)await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Signatures_come_back_oldest_first_regardless_of_insertion_order()
    {
        var store = new GoldpathEfApprovalStore<EfApprovalStoreTests.ApprovalsDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>());
        var requestId = Guid.NewGuid();
        var later = DateTimeOffset.Parse("2026-08-26T12:00:00Z");
        var earlier = DateTimeOffset.Parse("2026-08-26T09:00:00Z");
        await store.AddSignatureAsync(new GoldpathApprovalSignature(requestId, "manager-two", "manager", later));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(requestId, "manager-one", "manager", earlier));
        await store.AddSignatureAsync(new GoldpathApprovalSignature(Guid.NewGuid(), "stranger", "manager", earlier));

        var signatures = await store.GetSignaturesAsync(requestId);
        Assert.Equal(["manager-one", "manager-two"], signatures.Select(s => s.SignedBy));
        Assert.All(signatures, s => Assert.Equal(requestId, s.RequestId));
        Assert.All(signatures, s => Assert.Equal("manager", s.Role));
    }

    [Fact]
    public async Task A_delegation_expiring_exactly_now_admits_nobody()
    {
        var store = new GoldpathEfApprovalStore<EfApprovalStoreTests.ApprovalsDbContext>(
            _provider.GetRequiredService<IServiceScopeFactory>());
        var now = DateTimeOffset.Parse("2026-08-26T09:00:00Z");
        await store.AddDelegationAsync(new GoldpathApprovalDelegation("expert-user", "stand-in", now));

        Assert.Empty(await store.GetDelegationsAsync(now));   // Until > now is strict
        var stillActive = await store.GetDelegationsAsync(now - TimeSpan.FromSeconds(1));
        Assert.Equal("stand-in", Assert.Single(stillActive).To);
    }
}
