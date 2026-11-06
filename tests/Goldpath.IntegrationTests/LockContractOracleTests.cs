using Medallion.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// THE lock contract (Locking RFC §7) on a real Oracle Database Free container, through
/// <c>AddGoldpathOracleLocking</c> (G7, slice O-4, #407): the same two facts <see cref="LockContractTests"/> holds on
/// Redis and Postgres. The schema the lock runs in gets <c>EXECUTE ON SYS.DBMS_LOCK</c> exactly as
/// <c>packages/Goldpath.Locking.Oracle/ops/locking-oracle.md</c> tells a DBA to grant it, and a schema without the grant
/// fails the way that runbook says.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class LockContractOracleTests(OracleFixture oracle)
{
    private static int s_schemas;

    /// <summary>A fresh schema (Oracle user), granted <c>EXECUTE ON SYS.DBMS_LOCK</c> as SYSDBA when <paramref name="grant"/> is set.</summary>
    private async Task<string> SchemaAsync(bool grant)
    {
        var name = $"LOCKS{Interlocked.Increment(ref s_schemas)}_{Guid.NewGuid():N}"[..20].ToUpperInvariant();
        var connectionString = await oracle.CreateSchemaAsync(name);
        if (grant)
        {
            var sys = new OracleConnectionStringBuilder(oracle.ConnectionString) { UserID = "sys", DBAPrivilege = "SYSDBA" };
            await using var connection = new OracleConnection(sys.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"GRANT EXECUTE ON SYS.DBMS_LOCK TO {name}";   // the runbook's statement, verbatim
            await command.ExecuteNonQueryAsync();
        }

        return connectionString;
    }

    private static IHost BuildHost(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:database"] = connectionString;   // the package's default ConnectionName
        builder.AddGoldpathOracleLocking();
        return builder.Build();
    }

    [Fact]
    public async Task Exactly_one_competitor_holds_the_lock_and_release_hands_it_over()
    {
        using var host = BuildHost(await SchemaAsync(grant: true));
        var options = host.Services.GetRequiredService<GoldpathLockingOptions>();
        Assert.Equal(GoldpathLockProvider.Oracle, options.Provider);
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();
        Assert.IsType<GoldpathMeteredLockProvider>(locks);
        var name = GoldpathLockNames.Global("contract-oracle");

        // Two competitors: exactly one wins.
        var first = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(2));
        Assert.NotNull(first);
        var loser = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(2));
        Assert.Null(loser);                                     // timeout → null, never a throw

        // Release hands the lock over.
        await first.DisposeAsync();
        var second = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(5));
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task Tenant_scoped_names_do_not_contend_across_tenants()
    {
        using var host = BuildHost(await SchemaAsync(grant: true));
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();

        string forA, forB;
        using (GoldpathTenant.Use("tenant-a"))
        {
            forA = new GoldpathLockNames().For("nightly-report");
        }

        using (GoldpathTenant.Use("tenant-b"))
        {
            forB = new GoldpathLockNames().For("nightly-report");
        }

        await using var heldByA = await locks.TryAcquireLockAsync(forA, TimeSpan.FromSeconds(2));
        await using var heldByB = await locks.TryAcquireLockAsync(forB, TimeSpan.FromSeconds(2));

        Assert.NotNull(heldByA);
        Assert.NotNull(heldByB);   // same logical job, different tenants — no contention
    }

    [Fact]
    public async Task A_name_longer_than_Oracle_takes_is_still_one_exclusive_lock()
    {
        using var host = BuildHost(await SchemaAsync(grant: true));
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();
        var name = GoldpathLockNames.Global("long-" + new string('x', 300));   // DBMS_LOCK names stop at 128 bytes

        await using var first = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(2));
        Assert.NotNull(first);
        Assert.Null(await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(1)));

        // Two names that agree on their first 300 characters and differ in the last are two locks, not one
        // truncated name: the library hashes a name too long for DBMS_LOCK rather than cut it.
        await using var other = await locks.TryAcquireLockAsync(name[..^1] + "y", TimeSpan.FromSeconds(2));
        Assert.NotNull(other);
    }

    [Fact]
    public void A_provider_left_in_configuration_for_another_store_is_refused()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:database"] = oracle.ConnectionString;
        builder.Configuration["Goldpath:DistributedLocking:Provider"] = "Postgres";

        var refused = Assert.Throws<InvalidOperationException>(() => builder.AddGoldpathOracleLocking());
        Assert.Contains("says 'Postgres'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_grant_the_first_acquire_fails_naming_DBMS_LOCK()
    {
        using var host = BuildHost(await SchemaAsync(grant: false));
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();

        var refused = await Assert.ThrowsAnyAsync<Exception>(
            () => locks.TryAcquireLockAsync(GoldpathLockNames.Global("ungranted"), TimeSpan.FromSeconds(2)).AsTask());
        var message = refused.ToString();
        Assert.Contains("PLS-00201", message, StringComparison.Ordinal);
        Assert.Contains("DBMS_LOCK", message, StringComparison.Ordinal);
    }
}
