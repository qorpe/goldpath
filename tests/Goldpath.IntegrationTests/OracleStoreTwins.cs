using Goldpath.Approvals.Tests;
using Goldpath.FileExchange.Tests;
using Microsoft.EntityFrameworkCore;
using Oracle.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The Oracle twins of three EF store suites (G7, slice O-5b, #410; RFC goldpath-oracle §7). Each runs the SAME facts
/// file its unit project runs on SQLite — linked into this project, not copied — on the lane's one container, with a
/// schema of its own per fact, in the 19c SQL form the RFC ships.
/// </summary>
internal static class OracleTwin
{
    /// <summary>The provider configuration every twin uses: Oracle, in the 19c form.</summary>
    public static Action<DbContextOptionsBuilder> Use(string connection)
        => b => b.UseOracle(connection, o => o.UseOracleSQLCompatibility(OracleSQLCompatibility.DatabaseVersion19));
}

/// <summary><see cref="EfApprovalStoreFacts"/> on Oracle.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class EfApprovalStoreOracleTests(OracleFixture oracle) : EfApprovalStoreFacts
{
    private static int _schemas;

    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
        => OracleTwin.Use(await oracle.CreateSchemaAsync($"GP_APPR_{Interlocked.Increment(ref _schemas)}"));
}

/// <summary><see cref="EfFileLedgerFacts"/> on Oracle.</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class EfFileLedgerOracleTests(OracleFixture oracle) : EfFileLedgerFacts
{
    private static int _schemas;

    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
        => OracleTwin.Use(await oracle.CreateSchemaAsync($"GP_FILES_{Interlocked.Increment(ref _schemas)}"));
}

/// <summary>
/// <see cref="AuditTrailFacts"/> on Oracle: a fact that needs a host of its own takes a second schema. Every added entity
/// writes audit rows whose <c>OldValue</c> document is NULL, and #475 refuses a NULL document in the client, so the three
/// facts that write an audited entity assert that refusal until #475 lands.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class AuditTrailOracleTests(OracleFixture oracle) : AuditTrailFacts
{
    private static int _schemas;

    protected override string? AuditRowRefusedUnder => "#475";

    protected override async Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync()
        => OracleTwin.Use(await oracle.CreateSchemaAsync($"GP_AUDIT_{Interlocked.Increment(ref _schemas)}"));
}

/// <summary>
/// <see cref="NotificationFacts"/> on Oracle, with the Quartz store on Oracle too (O-3). The webhook alert's template exists
/// only in the invariant culture, whose name is the empty string, and #502 is that Oracle refuses it.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class NotificationOracleTests(OracleFixture oracle) : NotificationFacts
{
    private static int _schemas;

    protected override GoldpathJobStoreProvider JobStore => GoldpathJobStoreProvider.Oracle;

    protected override string? InvariantCultureRefusedUnder => "#502";

    protected override Task<string> StartDatabaseAsync()
        => oracle.CreateSchemaAsync($"GP_NOTIFY_{Interlocked.Increment(ref _schemas)}");

    protected override Task StopDatabaseAsync() => Task.CompletedTask;   // the collection's container is the fixture's

    protected override void Use(DbContextOptionsBuilder options, string connection) => OracleTwin.Use(connection)(options);
}
