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

    // ---- finding 2: an outcome is bound to the attempt it describes (constraint 2, R1.3 ladder) ----

    /// <summary>
    /// Finding 2 / constraint 2: a redelivered failure of attempt 1 that lands while attempt 2
    /// is in flight must not touch attempt 2 — and attempt 2's success must settle the item.
    /// </summary>
    [Fact]
    public async Task AStaleFailureOfAttemptOne_NeverReachesAttemptTwo()
    {
        using var fixture = new CampaignFixture(o => { o.RetryJitter = 0; o.RetryBackoff = [TimeSpan.Zero]; }, 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 5));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));

        var failure1 = new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, "attempt 1 timed out") { Retryable = true, Attempt = 1 };
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [failure1], CancellationToken.None);
        await Task.Delay(10);
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));   // attempt 2 in flight

        // Broker redelivery of attempt 1's failure, and a stale acceptance of attempt 1 — both no-ops now.
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [failure1], CancellationToken.None);
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id,
            [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, null) { Accepted = true, CorrelationId = "old", AckDeadline = DateTimeOffset.UtcNow.AddHours(1), Attempt = 1 }],
            CancellationToken.None);
        var inFlight = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.Processing, inFlight.State);
        Assert.Equal(1, inFlight.Attempts);
        Assert.Null(inFlight.CorrelationId);

        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id,
            [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, true, null) { Attempt = 2 }], CancellationToken.None);
        var row = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.Succeeded, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);
    }

    /// <summary>
    /// Finding 2: the consumer's own outcomes carry the attempt they describe — success,
    /// typed failure, acceptance and a handler throw alike.
    /// </summary>
    [Fact]
    public async Task EveryOutcomeShapeNamesItsAttempt()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var engine = fixture.Engine;
        var id = Guid.NewGuid();
        Assert.Equal(3, engine.OutcomeFor(id, 1, 3, GoldpathCampaignActionResult.Succeeded()).Attempt);
        Assert.Equal(3, engine.OutcomeFor(id, 1, 3, GoldpathCampaignActionResult.Failed("X", "x", retryable: true)).Attempt);
        Assert.Equal(3, engine.OutcomeFor(id, 1, 3, GoldpathCampaignActionResult.Accepted("c", TimeSpan.FromMinutes(1))).Attempt);
        await Task.CompletedTask;
    }

    // ---- finding 6: nobody starves under a shared ceiling (R2.5 "the push still moves") ----

    public static TheoryData<GoldpathCampaignPriority[]> PriorityMixes()
    {
        var all = new[] { GoldpathCampaignPriority.High, GoldpathCampaignPriority.Normal, GoldpathCampaignPriority.Low };
        var data = new TheoryData<GoldpathCampaignPriority[]>();
        for (var a = 0; a < 3; a++)
        {
            for (var b = a; b < 3; b++)
            {
                data.Add([all[a], all[b]]);
                for (var c = b; c < 3; c++)
                {
                    data.Add([all[a], all[b], all[c]]);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Finding 6 / R2.5 ("nobody starves"; the XML's "guaranteed its weighted share"): for
    /// every mix of priorities, every type ceiling from 1 to 25 per second (most of which do
    /// not divide the tick evenly) and three leader ticks, a hungry campaign is never more
    /// than one fair turn from its next unit, and its long-run share is its weight's share —
    /// within one unit per claimant. The bucket is simulated exactly as the pacer banks it.
    /// </summary>
    [Theory]
    [MemberData(nameof(PriorityMixes))]
    public void UnderASharedCeiling_EveryPriorityMixGetsItsWeightedShare_AndNobodyStarves(GoldpathCampaignPriority[] mix)
    {
        var weights = mix.Select(GoldpathCampaignFairShare.WeightOf).ToArray();
        var weightSum = weights.Sum();
        foreach (var tick in new[] { TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1) })
        {
            for (var maxTps = 1; maxTps <= 25; maxTps++)
            {
                var claims = weights.Select(w => new GoldpathCampaignFairShareClaim(w, 1_000)).ToArray();
                var credit = new double[claims.Length];
                var granted = new long[claims.Length];
                var handedSinceLast = new long[claims.Length];
                var budget = 0d;
                long handed = 0;
                for (var t = 1; t <= 2_000; t++)
                {
                    budget = Math.Min(budget + maxTps * tick.TotalSeconds, maxTps);
                    var grants = GoldpathCampaignFairShare.Allocate(claims, (int)Math.Floor(budget), credit, t % claims.Length);
                    var units = grants.Sum();
                    budget -= units;
                    handed += units;
                    for (var i = 0; i < claims.Length; i++)
                    {
                        handedSinceLast[i] += units;
                        granted[i] += grants[i];
                        if (grants[i] > 0)
                        {
                            handedSinceLast[i] = 0;
                        }

                        // Nobody starves: between two of its units, no more than one fair
                        // turn (the units it takes for its weight to come round) plus one per rival.
                        Assert.True(handedSinceLast[i] <= (weightSum / weights[i]) + claims.Length,
                            $"{string.Join("/", mix)} at MaxTps {maxTps}, tick {tick.TotalMilliseconds}ms: claimant {i} waited {handedSinceLast[i]} units");
                    }
                }

                for (var i = 0; i < claims.Length; i++)
                {
                    var exact = (double)handed * weights[i] / weightSum;
                    Assert.True(Math.Abs(granted[i] - exact) <= 1.0,
                        $"{string.Join("/", mix)} at MaxTps {maxTps}, tick {tick.TotalMilliseconds}ms: claimant {i} got {granted[i]}, its share is {exact:F2}");
                }
            }
        }
    }

    /// <summary>
    /// Finding 6 / R2.5: the reviewer's two cases on ONE tick — High(3) vs Low(1) on three
    /// units and High(3) vs Normal(2) on two — each give the lighter campaign a unit.
    /// </summary>
    [Fact]
    public void TheLighterCampaignGetsAUnit_EvenOnASmallTick()
    {
        Assert.Equal([2, 1], GoldpathCampaignFairShare.Allocate([new(3, 100), new(1, 100)], 3));
        Assert.Equal([1, 1], GoldpathCampaignFairShare.Allocate([new(3, 100), new(2, 100)], 2));
    }

    /// <summary>
    /// Finding 6 / R2.5 on the real pacer: a type ceiling of three units per tick, High
    /// against Low. The Low campaign moves, and the pacer carries the fair-share credit from
    /// tick to tick, so the split is 3:1 — not 3:0 (the bug) and not 2:1 (credit forgotten).
    /// </summary>
    [Fact]
    public async Task OnTheRealPacer_ALowCampaignUnderAThreeUnitCeiling_MovesAtItsShare()
    {
        // MaxTps 150 at a 20 ms tick = three units a tick, the shape of MaxTps 12 at the default 250 ms.
        using var fixture = new CampaignFixture(R2Tests.FastTicks(o =>
        {
            o.EnumerationBatchSize = 500;
            o.AddCampaign<TestTarget>("device", c => c.MaxTargets(10_000).MaxTps(150).Targets((_, _) => Targets(2_000)));
        }));
        var high = await CreateEnumeratedAsync(fixture, "device", GoldpathCampaignPriority.High);
        var low = await CreateEnumeratedAsync(fixture, "device", GoldpathCampaignPriority.Low);
        await fixture.RunPacerSliceAsync();

        var releasedHigh = fixture.Reload(high).ReleasedThrough;
        var releasedLow = fixture.Reload(low).ReleasedThrough;
        var total = releasedHigh + releasedLow;
        Assert.True(total >= 24, $"expected at least eight ticks of three units, saw {total}");
        Assert.InRange(releasedLow, (total / 4.0) - 1.5, (total / 4.0) + 1.5);
    }

    /// <summary>R2.5: on an exact tie in "how far behind", the heavier claimant goes first — then the rotation.</summary>
    [Fact]
    public void OnAnExactTie_TheHeavierClaimantGoesFirst()
    {
        // Shares 1/3 and 2/3 of one unit; the light one carries 1/3 — both are exactly 2/3 behind.
        var credit = new[] { 1.0 / 3, 0 };
        Assert.Equal([0, 1], GoldpathCampaignFairShare.Allocate([new(1, 9), new(2, 9)], 1, credit, tieBreakStart: 0));
    }

    /// <summary>R2.5: nothing is banked while not asking, nor when the budget covers everyone — not even at exactly the budget.</summary>
    [Fact]
    public void CreditResetsWhenIdle_AndWhenTheBudgetCoversEveryDemand()
    {
        var idle = new[] { 0.0, 1.5 };
        GoldpathCampaignFairShare.Allocate([new(2, 9), new(2, 0)], 1, idle);
        Assert.Equal(0, idle[1]);

        var covered = new[] { 0.7, -0.7 };
        Assert.Equal([2, 3], GoldpathCampaignFairShare.Allocate([new(2, 2), new(1, 3)], 5, covered));   // demand == budget
        Assert.Equal([0.0, 0.0], covered);
    }

    /// <summary>R2.5: a claimant that asks for nothing needs no weight; a credit array of the wrong length is refused.</summary>
    [Fact]
    public void AnIdleClaimNeedsNoWeight_AndCreditMustMatchTheClaims()
    {
        Assert.Equal([0, 3], GoldpathCampaignFairShare.Allocate([new(0, 0), new(2, 9)], 3));
        Assert.Throws<ArgumentException>(() => GoldpathCampaignFairShare.Allocate([new(2, 9)], 3, new double[2]));
    }

    // ---- finding 8: what is in flight drains, whatever the campaign's state (RFC §6 abort, R2.1 ack sweep) ----

    /// <summary>
    /// Finding 8 / RFC §6 ("claimed items drain") + R2.1 ("wait for it, but not forever"): an
    /// acceptance nobody answers on an ABORTED campaign still meets its deadline — as a
    /// terminal ACK_TIMEOUT failure filed for repair, never re-laddered and never stuck.
    /// </summary>
    [Fact]
    public async Task AnAbortedCampaignsUnansweredAcceptance_FailsAtItsDeadline_AndIsFiledForRepair()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("never-1", TimeSpan.FromMilliseconds(50));
        var campaign = await AcceptOneAsync(fixture, maxAttempts: 3);
        Assert.True((await fixture.Admin().AbortAsync(campaign.Id, "wrong list", "op", CancellationToken.None)).Ok);
        await Task.Delay(100);

        var filed = await fixture.RunPacerSliceAsync();

        var item = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.Failed, item.State);
        Assert.Equal("ACK_TIMEOUT", item.ErrorCode);
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        Assert.Contains(filed, f => f.ItemKey == $"{campaign.Id:N}#1");
    }

    /// <summary>
    /// Finding 8 / RFC D5 stale-claim repair: a consumer that died mid-call on a PAUSED
    /// campaign is swept to the repair queue too — a pause must not hide an interrupted claim.
    /// </summary>
    [Fact]
    public async Task APausedCampaignsStaleClaim_IsSweptForRepair()
    {
        using var fixture = new CampaignFixture(R2Tests.FastTicks(o => o.StaleClaimAfter = TimeSpan.Zero), sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
            Assert.NotNull(await fixture.Engine.ClaimAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), campaign.Id, 1, CancellationToken.None));
        }

        Assert.True((await fixture.Admin().PauseAsync(campaign.Id, "op", CancellationToken.None)).Ok);
        await Task.Delay(20);
        var filed = await fixture.RunPacerSliceAsync();

        Assert.Equal(GoldpathCampaignItemState.Failed, SingleItem(fixture).State);
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        Assert.Contains(filed, f => f.ItemKey == $"{campaign.Id:N}#1");
    }

    /// <summary>
    /// Finding 8 / R2.1 + the repair story: a replay on a FINISHED campaign whose handler
    /// answers Accepted, never called back, ends Failed(ACK_TIMEOUT) — the ladder only runs
    /// for a campaign the pacer still releases, so it must not park the item there forever.
    /// </summary>
    [Fact]
    public async Task AnAcceptedReplayOnAFinishedCampaign_TimesOutIntoFailed_NotTheLadder()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Failed("UNSUPPORTED_DEVICE", "no client", retryable: false);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.RunPacerSliceAsync();
        await fixture.ConsumeAllPublishedAsync();
        await fixture.RunPacerSliceAsync();
        Assert.Equal(GoldpathCampaignState.CompletedWithFailures, fixture.Reload(campaign.Id).State);

        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("replay-1", TimeSpan.FromMilliseconds(50));
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, $"{campaign.Id:N}#1", CancellationToken.None);
        }

        Assert.Equal(GoldpathCampaignItemState.AwaitingAck, SingleItem(fixture).State);
        Assert.Equal(0, fixture.Reload(campaign.Id).FailedCount);
        await Task.Delay(100);
        await fixture.RunPacerSliceAsync();

        var item = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.Failed, item.State);
        Assert.Equal("ACK_TIMEOUT", item.ErrorCode);
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
    }

    /// <summary>
    /// Finding 8 / R1.1 ("in-flight items still drain"): a closed window stops RELEASES, not
    /// the ack sweep — an overdue acceptance walks its ladder overnight instead of holding
    /// an in-flight slot until morning.
    /// </summary>
    [Fact]
    public async Task AClosedWindow_StillSweepsOverdueAcceptances()
    {
        using var fixture = R2Tests.ActionFixture(o => o.RetryJitter = 0, sourceSize: 1);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("night-1", TimeSpan.FromMilliseconds(50));
        var campaign = await AcceptOneAsync(fixture, maxAttempts: 3);
        var hour = DateTime.UtcNow.Hour;
        Assert.True((await fixture.Admin().ThrottleAsync(campaign.Id, new GoldpathCampaignThrottle(
            WindowStart: new TimeOnly((hour + 2) % 24, 0), WindowEnd: new TimeOnly((hour + 3) % 24, 0), TimeZoneId: "UTC"), "op", CancellationToken.None)).Ok);
        await Task.Delay(100);

        await fixture.RunPacerSliceAsync();

        var item = SingleItem(fixture);
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);   // still Running: the ladder applies
        Assert.Equal("ACK_TIMEOUT", item.ErrorCode);
    }

    // ---- helpers ----

    /// <summary>Creates, enumerates, releases and consumes one item whose scripted handler answers Accepted.</summary>
    private static async Task<GoldpathCampaign> AcceptOneAsync(CampaignFixture fixture, int maxAttempts)
    {
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts));
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(GoldpathCampaignItemState.AwaitingAck, SingleItem(fixture).State);
        return campaign;
    }

    private static async Task<Guid> CreateEnumeratedAsync(CampaignFixture fixture, string type, GoldpathCampaignPriority priority)
    {
        GoldpathCampaign campaign;
        using (var scope = fixture.Services.CreateScope())
        {
            campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, type, priority.ToString(), new Dictionary<string, string>(),
                new GoldpathCampaignPolicy(10_000, null, 100_000, null, null, "UTC") { Priority = priority }, tenant: null, actor: "tester", CancellationToken.None);
        }

        await fixture.EnumerateAllAsync(campaign);
        return campaign.Id;
    }

    private static async IAsyncEnumerable<TestTarget> Targets(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new TestTarget(i, $"device{i}@example.test");
        }

        await Task.CompletedTask;
    }

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
