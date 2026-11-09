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
        // truncated name: a name too long for DBMS_LOCK is hashed (OracleLockNames), never cut.
        await using var other = await locks.TryAcquireLockAsync(name[..^1] + "y", TimeSpan.FromSeconds(2));
        Assert.NotNull(other);
    }

    [Fact]
    public async Task A_name_under_128_characters_but_over_128_bytes_is_still_one_exclusive_lock()
    {
        using var host = BuildHost(await SchemaAsync(grant: true));
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();

        // 87 characters, 148 UTF-8 bytes: the library counts characters and passes it through, and
        // DBMS_LOCK_ALLOCATED.NAME (128 BYTES) refused it with ORA-12899 before OracleLockNames.
        var name = GoldpathLockNames.Global("işlem-" + new string('İ', 60));
        Assert.True(name.Length <= 128 && System.Text.Encoding.UTF8.GetByteCount(name) > 128);

        await using var first = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(2));
        Assert.NotNull(first);
        Assert.Null(await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(1)));   // timeout → null, never a throw

        // A name differing only in its last character is a second lock.
        await using var other = await locks.TryAcquireLockAsync(name[..^1] + "ı", TimeSpan.FromSeconds(2));
        Assert.NotNull(other);

        // And a long multi-byte name (over 128 characters too) — the library's own hash keeps a prefix of
        // the name, which in non-ASCII would exceed the bytes again.
        await using var longer = await locks.TryAcquireLockAsync(GoldpathLockNames.Global(new string('ş', 300)), TimeSpan.FromSeconds(2));
        Assert.NotNull(longer);
    }

    /// <summary>
    /// Pins what the runbook's "Cancellation and shutdown" section says: on Oracle a cancelled acquire does not
    /// return when its token is cancelled. <c>DBMS_LOCK.REQUEST</c> is a server-side wait the library does not
    /// interrupt, so the token is observed only once that wait ends — up to the acquire timeout. If a library
    /// upgrade starts honouring cancellation, this fact goes red, and the runbook section is what to revisit.
    /// </summary>
    [Fact]
    public async Task A_cancelled_acquire_on_a_held_lock_waits_out_its_timeout_before_it_observes_the_token()
    {
        using var host = BuildHost(await SchemaAsync(grant: true));
        var locks = host.Services.GetRequiredService<IDistributedLockProvider>();
        var name = GoldpathLockNames.Global("cancel-oracle");
        var timeout = TimeSpan.FromSeconds(8);

        await using var held = await locks.TryAcquireLockAsync(name, TimeSpan.FromSeconds(2));
        Assert.NotNull(held);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => locks.TryAcquireLockAsync(name, timeout, cts.Token).AsTask());
        watch.Stop();

        // Cancelled at 0.5 s, observed only after the server-side wait: well past the cancellation point.
        Assert.True(watch.Elapsed >= timeout - TimeSpan.FromSeconds(2), $"returned after {watch.Elapsed}");
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
