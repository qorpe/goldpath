using Microsoft.EntityFrameworkCore.Migrations;

namespace Goldpath;

/// <summary>
/// Data steps a campaign revision needs on top of the schema EF generates. Migrations live
/// in the APP (migrations RFC D1), so a package cannot put a data step into one; it ships the
/// step as a call the generated migration makes.
/// </summary>
public static class GoldpathCampaignMigrations
{
    /// <summary>
    /// Campaign revision R2's data step: stamps <c>NextAttemptAt</c> on every
    /// <see cref="GoldpathCampaignItemState.AwaitingRetry"/> row that predates the column, with
    /// the R1 rung it was waiting on — 30 seconds after its first failure, 2 minutes after a
    /// later one, measured from <c>CompletedAt</c> (R1's "last failure instant"). Call it at the
    /// end of <c>Up</c> in the migration <c>goldpath db add CampaignR2R3</c> generated. It touches
    /// only rows still waiting, so it is cheap and safe to run twice.
    /// <para>
    /// It also carries revision R3's one data step (#384): a global ceiling row that an earlier
    /// build wrote with the empty name moves to <see cref="GoldpathCampaignCeiling.GlobalName"/>;
    /// when a <c>"*"</c> row already exists, the empty-named one is the stale copy and is deleted
    /// instead. It matches only an empty-named global row, so a second run changes nothing. On Oracle there is no
    /// such step: Oracle stores <c>''</c> as NULL, so its key column never held an empty name.
    /// </para>
    /// <para>
    /// PostgreSQL, SQL Server and Oracle are supported (the provider is read from
    /// <see cref="MigrationBuilder.ActiveProvider"/>); any other provider is refused with the stamp to write by hand.
    /// </para>
    /// </summary>
    /// <param name="migrationBuilder">The generated migration's builder.</param>
    /// <param name="schema">The schema the campaign tables live in, when the model sets one.</param>
    public static MigrationBuilder GoldpathCampaignR2Backfill(this MigrationBuilder migrationBuilder, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        const int awaitingRetry = (int)GoldpathCampaignItemState.AwaitingRetry;
        const int global = (int)GoldpathCampaignCeilingKind.Global;
        const string globalName = GoldpathCampaignCeiling.GlobalName;
        var provider = migrationBuilder.ActiveProvider ?? "";
        string sql;
        string ceilingSql;
        if (provider.Contains("Npgsql", StringComparison.Ordinal) || provider.Contains("PostgreSQL", StringComparison.Ordinal))
        {
            var table = schema is null ? "\"GoldpathCampaignItems\"" : $"\"{schema}\".\"GoldpathCampaignItems\"";
            var ceilings = schema is null ? "\"GoldpathCampaignCeilings\"" : $"\"{schema}\".\"GoldpathCampaignCeilings\"";
            ceilingSql = $"""
                DELETE FROM {ceilings}
                WHERE "Kind" = {global} AND "Name" = ''
                  AND EXISTS (SELECT 1 FROM {ceilings} WHERE "Kind" = {global} AND "Name" = '{globalName}');
                UPDATE {ceilings} SET "Name" = '{globalName}' WHERE "Kind" = {global} AND "Name" = '';
                """;
            sql = $"""
                UPDATE {table}
                SET "NextAttemptAt" = COALESCE("CompletedAt", now())
                    + CASE WHEN "Attempts" <= 1 THEN interval '30 seconds' ELSE interval '2 minutes' END
                WHERE "State" = {awaitingRetry} AND "NextAttemptAt" IS NULL;
                """;
        }
        else if (provider.Contains("SqlServer", StringComparison.Ordinal))
        {
            var table = schema is null ? "[GoldpathCampaignItems]" : $"[{schema}].[GoldpathCampaignItems]";
            var ceilings = schema is null ? "[GoldpathCampaignCeilings]" : $"[{schema}].[GoldpathCampaignCeilings]";
            ceilingSql = $"""
                DELETE FROM {ceilings}
                WHERE [Kind] = {global} AND [Name] = N''
                  AND EXISTS (SELECT 1 FROM {ceilings} WHERE [Kind] = {global} AND [Name] = N'{globalName}');
                UPDATE {ceilings} SET [Name] = N'{globalName}' WHERE [Kind] = {global} AND [Name] = N'';
                """;
            sql = $"""
                UPDATE {table}
                SET [NextAttemptAt] = DATEADD(second, CASE WHEN [Attempts] <= 1 THEN 30 ELSE 120 END, COALESCE([CompletedAt], SYSDATETIMEOFFSET()))
                WHERE [State] = {awaitingRetry} AND [NextAttemptAt] IS NULL;
                """;
        }
        else if (provider.Contains("Oracle", StringComparison.Ordinal))
        {
            // RFC goldpath-oracle §2.7. ODP.NET runs one statement per command and refuses a trailing ';'.
            var table = schema is null ? "\"GoldpathCampaignItems\"" : $"\"{schema}\".\"GoldpathCampaignItems\"";
            migrationBuilder.Sql($"""
                UPDATE {table}
                SET "NextAttemptAt" = COALESCE("CompletedAt", SYSTIMESTAMP)
                    + CASE WHEN "Attempts" <= 1 THEN INTERVAL '30' SECOND ELSE INTERVAL '2' MINUTE END
                WHERE "State" = {awaitingRetry} AND "NextAttemptAt" IS NULL
                """);

            // No ceiling step: Oracle stores '' as NULL, so no build ever wrote an empty-named global row there —
            // the key column refused it (#384). There is nothing to move.
            return migrationBuilder;
        }
        else
        {
            throw new NotSupportedException(
                $"goldpath: no campaign R2 backfill for provider '{provider}' — stamp NextAttemptAt on AwaitingRetry ({awaitingRetry}) rows "
                + "yourself (CompletedAt + 30s after the first failure, + 2m after later ones); until you do, the engine treats them as ripe.");
        }

        migrationBuilder.Sql(sql);
        migrationBuilder.Sql(ceilingSql);
        return migrationBuilder;
    }
}
