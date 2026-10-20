using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// The R2 review's findings (2026-10-20), each turned into the regression test that asserts
/// the CORRECT behaviour. Every test names the finding and the rule it proves.
/// </summary>
public class R2ReviewTests
{
    // ---- finding 5: a replay heals the ledger on its OUTCOME, never before (RFC D5 "repair, not requeue") ----

    /// <summary>
    /// Finding 5 / RFC D5: a replay whose handler throws leaves the item Failed and the
    /// failed count where it was — re-driving it twice must not drift the ledger.
    /// </summary>
    [Fact]
    public async Task AReplayWhoseHandlerThrows_LeavesTheItemFailed_AndTheLedgerTrue()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        fixture.FailIds.Add(1);
        var campaign = await FailOneItemAsync(fixture);

        var key = $"{campaign.Id:N}#1";
        for (var i = 0; i < 2; i++)
        {
            using var scope = fixture.Services.CreateScope();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Engine.ReplayItemAsync(scope.ServiceProvider, key, CancellationToken.None));
        }

        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        Assert.Equal(GoldpathCampaignItemState.Failed, SingleItem(fixture).State);

        // The provider recovered: the next replay heals the ledger exactly once.
        fixture.FailIds.Clear();
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, key, CancellationToken.None);
        }

        var healed = fixture.Reload(campaign.Id);
        Assert.Equal(0, healed.FailedCount);
        Assert.Equal(1, healed.SucceededCount);
        Assert.Equal(GoldpathCampaignItemState.Succeeded, SingleItem(fixture).State);
    }

    /// <summary>
    /// Finding 5 / constraint 2 (claim before execute): a replay never steals an item a
    /// consumer owns — only a FAILED item can be re-claimed by the repair verb.
    /// </summary>
    [Fact]
    public async Task AReplayNeverStealsAnItemSomeoneElseOwns()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));   // a consumer is mid-call

        await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, $"{campaign.Id:N}#1", CancellationToken.None);

        Assert.Empty(fixture.Executed);
        Assert.Equal(GoldpathCampaignItemState.Processing, SingleItem(fixture).State);
        Assert.Equal(0, fixture.Reload(campaign.Id).FailedCount);
    }

    // ---- finding 4: a waiting retry that predates R2 is never stranded (R1.3 ladder, R2.3 ripe instant) ----

    /// <summary>
    /// Finding 4 / R1.3 + R2.3: an AwaitingRetry row written by R1 carries no NextAttemptAt.
    /// It must still be released and claimed once ripe — never held forever as "in flight".
    /// </summary>
    [Fact]
    public async Task AnR1WaitingRetry_WithNoRipeInstant_IsReleasedAndClaimed()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        StampAsR1Retry(fixture);

        using var scope = fixture.Services.CreateScope();
        var running = fixture.Reload(campaign.Id);
        Assert.Equal(1, await fixture.Engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, running, 100, CancellationToken.None));
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
    }

    /// <summary>
    /// Finding 4 / R1.3: the same R1-shaped row whose message outran the mark claims too —
    /// the claim guard reads the same ripeness as the release query.
    /// </summary>
    [Fact]
    public async Task AnR1WaitingRetry_ClaimsEvenBeforeTheReleaseMarkLands()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        StampAsR1Retry(fixture);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
    }

    /// <summary>
    /// Finding 4 / RFC §6 abort semantics: a retry waiting on its rung is UNSENT work —
    /// abort stamps it Aborted and clears its ripe instant, like Pending and Released.
    /// </summary>
    [Fact]
    public async Task AbortStampsWaitingRetries_AndClearsTheirRipeInstant()
    {
        using var fixture = new CampaignFixture(R2Tests.FastTicks(o => o.RetryJitter = 0), sourceSize: 1);
        fixture.FailIds.Add(1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, SingleItem(fixture).State);

        Assert.True((await fixture.Admin().AbortAsync(campaign.Id, "wrong list", "op", CancellationToken.None)).Ok);
        var item = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.Aborted, item.State);
        Assert.Null(item.NextAttemptAt);
    }

    /// <summary>
    /// Finding 4 / migrations runbook §3: the CampaignR2 migration's data step stamps every
    /// R1 waiting retry with the R1 rung (30s after the first failure, 2m after later ones),
    /// per provider — the code's null-is-ripe rule is the safety net, not the plan.
    /// </summary>
    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "UPDATE \"GoldpathCampaignItems\"", "interval '30 seconds'", "\"NextAttemptAt\" IS NULL")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "UPDATE [GoldpathCampaignItems]", "DATEADD(second", "[NextAttemptAt] IS NULL")]
    public void TheR2BackfillStampsTheR1Rung_PerProvider(string provider, string table, string rung, string onlyUnstamped)
    {
        var migration = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder(provider);
        migration.GoldpathCampaignR2Backfill();
        var sql = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(Assert.Single(migration.Operations)).Sql;
        Assert.Contains(table, sql, StringComparison.Ordinal);
        Assert.Contains(rung, sql, StringComparison.Ordinal);
        Assert.Contains(onlyUnstamped, sql, StringComparison.Ordinal);
        Assert.Contains("= 6", sql, StringComparison.Ordinal);   // AwaitingRetry only
    }

    /// <summary>Finding 4: a schema-qualified model gets a schema-qualified backfill; an unknown provider is told, not guessed.</summary>
    [Fact]
    public void TheR2BackfillHonoursASchema_AndRefusesAnUnknownProvider()
    {
        var migration = new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        migration.GoldpathCampaignR2Backfill(schema: "ops");
        Assert.Contains("\"ops\".\"GoldpathCampaignItems\"",
            ((Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation)migration.Operations.Single()).Sql, StringComparison.Ordinal);

        var error = Assert.Throws<NotSupportedException>(() =>
            new Microsoft.EntityFrameworkCore.Migrations.MigrationBuilder("Some.Other.Provider").GoldpathCampaignR2Backfill());
        Assert.Contains("NextAttemptAt", error.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>Writes the single item as R1 left a transient failure: AwaitingRetry, no NextAttemptAt.</summary>
    private static void StampAsR1Retry(CampaignFixture fixture)
        => fixture.Mutate(db =>
        {
            var item = db.Set<GoldpathCampaignItem>().Single();
            item.State = GoldpathCampaignItemState.AwaitingRetry;
            item.Attempts = 1;
            item.CompletedAt = DateTimeOffset.UtcNow.AddHours(-1);
            item.NextAttemptAt = null;
        });

    private static async Task<GoldpathCampaign> FailOneItemAsync(CampaignFixture fixture)
    {
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        return campaign;
    }

    private static GoldpathCampaignItem SingleItem(CampaignFixture fixture)
        => fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single());
}
