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

    // ---- finding 3: a keyset takeover of a row with no key re-sends nothing (R2.7, constraint 3) ----

    /// <summary>
    /// Finding 3 / R2.7: a campaign enumerated by count (before R2, or while its type was
    /// count-based) carries no key. Switching the type to TargetsAfter must resume after the
    /// target already materialized LAST — derived from that row — never from the beginning.
    /// </summary>
    [Fact]
    public async Task AKeysetTakeoverOfAKeylessRow_ResumesAfterTheLastMaterializedTarget()
    {
        var openings = new List<string?>();
        using var fixture = new CampaignFixture(Keyed(10, openings));
        var campaign = await StepOnceAsync(fixture);

        // The row as a count registration left it: four items, no key.
        fixture.Mutate(db => db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id).EnumeratedKey = null);
        await fixture.EnumerateAllAsync(fixture.Reload(campaign.Id));

        var items = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().OrderBy(i => i.Seq).ToList());
        Assert.Equal(10, items.Count);                                          // ten targets, ten items
        Assert.Equal(10, items.Select(i => i.TargetJson).Distinct().Count());   // nobody twice
        Assert.Equal([null, "4"], openings);                                    // reopened after the derived key
        Assert.Equal("10", fixture.Reload(campaign.Id).EnumeratedKey);
    }

    /// <summary>
    /// Finding 3 / R2.7: when the last materialized row cannot name its key, the takeover
    /// refuses with teaching text rather than guess — and writes nothing.
    /// </summary>
    [Fact]
    public async Task AKeysetTakeoverThatCannotDeriveItsKey_RefusesAndWritesNothing()
    {
        var openings = new List<string?>();
        using var fixture = new CampaignFixture(Keyed(10, openings));
        var campaign = await StepOnceAsync(fixture);
        fixture.Mutate(db =>
        {
            db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id).EnumeratedKey = null;
            db.Set<GoldpathCampaignItem>().Single(i => i.Seq == 4).TargetJson = "null";
        });

        using var scope = fixture.Services.CreateScope();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), CancellationToken.None));
        Assert.Contains("resume key", error.Message, StringComparison.Ordinal);
        Assert.Equal(4, fixture.Query(db => db.Set<GoldpathCampaignItem>().Count()));
        Assert.Equal([null], openings);
    }

    // ---- helpers ----

    private static Action<GoldpathCampaignOptions> Keyed(int count, List<string?> openings)
        => o =>
        {
            o.EnumerationBatchSize = 4;
            o.AddCampaign<TestTarget>("keyed", c => c
                .MaxTargets(10_000)
                .TargetsAfter((_, _, after) =>
                {
                    openings.Add(after);
                    return After(count, after);
                }, target => target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };

    /// <summary>Creates a keyed campaign and materializes ONE step (4 items) through a leader that then dies.</summary>
    private static async Task<GoldpathCampaign> StepOnceAsync(CampaignFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, "keyed", "keyed run", new Dictionary<string, string>(),
            R2Tests.Generous(), tenant: null, actor: "tester", CancellationToken.None);
        var stream = await fixture.Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
        await fixture.Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
        await stream.DisposeAsync();
        Assert.Equal(4, fixture.Reload(campaign.Id).EnumeratedThrough);
        return campaign;
    }

    private static async IAsyncEnumerable<TestTarget> After(int count, string? after)
    {
        var from = after is null ? 1 : int.Parse(after, System.Globalization.CultureInfo.InvariantCulture) + 1;
        for (var i = from; i <= count; i++)
        {
            yield return new TestTarget(i, $"keyed{i}@example.test");
        }

        await Task.CompletedTask;
    }

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
