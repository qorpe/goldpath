namespace Goldpath.Tests.Integration;

/// <summary>
/// What a bench's printed line says about the database it measured (#556). PostgreSQL lines stay exactly as they were
/// before the Oracle column existed; an Oracle line ends in <c> provider=oracle</c>, and the bench scripts refuse an
/// Oracle run whose output has no such line, so a run that silently measured PostgreSQL cannot pass for Oracle.
/// </summary>
internal static class BenchProvider
{
    public const string OracleTag = " provider=oracle";

    public static string Tag(SuiteStore store) => store.IsOracle ? OracleTag : "";
}
