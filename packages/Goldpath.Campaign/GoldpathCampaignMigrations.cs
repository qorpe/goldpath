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
    /// end of <c>Up</c> in the migration <c>goldpath db add CampaignR2</c> generated. It touches
    /// only rows still waiting, so it is cheap and safe to run twice.
    /// </summary>
    /// <param name="migrationBuilder">The generated migration's builder.</param>
    /// <param name="schema">The schema the campaign tables live in, when the model sets one.</param>
    public static MigrationBuilder GoldpathCampaignR2Backfill(this MigrationBuilder migrationBuilder, string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        const int awaitingRetry = (int)GoldpathCampaignItemState.AwaitingRetry;
        var provider = migrationBuilder.ActiveProvider ?? "";
        string sql;
        if (provider.Contains("Npgsql", StringComparison.Ordinal) || provider.Contains("PostgreSQL", StringComparison.Ordinal))
        {
            var table = schema is null ? "\"GoldpathCampaignItems\"" : $"\"{schema}\".\"GoldpathCampaignItems\"";
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
            sql = $"""
                UPDATE {table}
                SET [NextAttemptAt] = DATEADD(second, CASE WHEN [Attempts] <= 1 THEN 30 ELSE 120 END, COALESCE([CompletedAt], SYSDATETIMEOFFSET()))
                WHERE [State] = {awaitingRetry} AND [NextAttemptAt] IS NULL;
                """;
        }
        else
        {
            throw new NotSupportedException(
                $"goldpath: no campaign R2 backfill for provider '{provider}' — stamp NextAttemptAt on AwaitingRetry ({awaitingRetry}) rows "
                + "yourself (CompletedAt + 30s after the first failure, + 2m after later ones); until you do, the engine treats them as ripe.");
        }

        migrationBuilder.Sql(sql);
        return migrationBuilder;
    }
}
