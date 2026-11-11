using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Revision R3.2, part a (#290): target systems as data. Which systems exist and which types call
/// them is code; how fast one may be called is a <see cref="GoldpathCampaignCeiling"/> row the leader
/// seeds from code and an operator changes live. Campaigns of DIFFERENT types naming one system share
/// its bucket by R2.5's weighted fair share, nested between R2.4's type bucket and R1.4's global one.
/// </summary>
public class R3TargetSystemTests
{
    private const string System = "device-core";

    private static Action<GoldpathCampaignOptions> FastTicks(Action<GoldpathCampaignOptions>? extra = null, int sliceMs = 400)
        => o =>
        {
            o.LeadershipSlice = TimeSpan.FromMilliseconds(sliceMs);
            o.LeaderTick = TimeSpan.FromMilliseconds(20);
            o.EnumerationBatchSize = 500;
            extra?.Invoke(o);
        };

    private static GoldpathCampaignPolicy Generous(GoldpathCampaignPriority priority = GoldpathCampaignPriority.Normal)
        => new(10_000, null, 100_000, null, null, "UTC") { Priority = priority };

    /// <summary>Two types — config-push and firmware-push — that call one device core.</summary>
    private static Action<GoldpathCampaignOptions> TwoTypesOnOneSystem(int maxTps, Action<GoldpathCampaignOptions>? extra = null, int sliceMs = 400)
        => FastTicks(o =>
        {
            o.AddTargetSystem(System, maxTps);
            o.AddCampaign<TestTarget>("config-push", c => c.MaxTargets(10_000).TargetSystem(System).Targets((_, _) => Targets(4_000)));
            o.AddCampaign<TestTarget>("firmware-push", c => c.MaxTargets(10_000).TargetSystem(System).Targets((_, _) => Targets(4_000)));
            extra?.Invoke(o);
        }, sliceMs);

    // ---- the declaration ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ATargetSystemNeedsAName(string name)
    {
        Assert.Throws<ArgumentException>(() => new GoldpathCampaignOptions().AddTargetSystem(name, 10));
        Assert.Throws<ArgumentException>(() => new GoldpathCampaignOptions()
            .AddCampaign<TestTarget>("t", c => c.MaxTargets(1).TargetSystem(name).Targets((_, _) => Targets(1))));
    }

    [Fact]
    public void ANameLongerThanTheRowHoldsIsRefused()
    {
        var options = new GoldpathCampaignOptions();
        options.AddTargetSystem(new string('a', 128), 10);   // exactly the column's length is fine
        var refusal = Assert.Throws<ArgumentException>(() => options.AddTargetSystem(new string('b', 129), 10));
        Assert.Contains("128", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ATargetSystemCeilingMustBePositive(int maxTps)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new GoldpathCampaignOptions().AddTargetSystem(System, maxTps));

    [Theory]
    [InlineData("device-core")]
    [InlineData("DEVICE-CORE")]
    public void ASystemIsDeclaredOnce_ComparedIgnoringCase(string again)
    {
        // A case-insensitive collation (SQL Server's default) would make the two one row; refusing here keeps every provider alike.
        var options = new GoldpathCampaignOptions().AddTargetSystem(System, 10);
        Assert.Throws<ArgumentException>(() => options.AddTargetSystem(again, 20));
        Assert.Equal(10, options.TargetSystems[System]);
    }

    [Fact]
    public void CaseFoldingIsCultureBlind_UnderTurkish()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new global::System.Globalization.CultureInfo("tr-TR");
        try
        {
            // Under tr-TR "i".ToUpper() is "İ": a culture-aware fold would call these two different names.
            var options = new GoldpathCampaignOptions().AddTargetSystem("billing", 10);
            Assert.Throws<ArgumentException>(() => options.AddTargetSystem("BILLING", 10));
            options.AddTargetSystem("bİlling", 10);   // a dotted capital İ is a different name, ordinally
            Assert.Equal(2, options.TargetSystems.Count);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AnUndeclaredSystemRefusesAtStartup_WithTeachingText()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var refusal = Assert.Throws<InvalidOperationException>(() => builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTestContext>(o =>
            o.AddCampaign<TestTarget>("config-push", c => c.MaxTargets(10).TargetSystem(System).Targets((_, _) => Targets(1)))));
        Assert.Contains("'config-push'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"'{System}'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("No target system is declared", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"AddTargetSystem(\"{System}\"", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledSystemIsNamed_WithTheDeclaredSpelling()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var refusal = Assert.Throws<InvalidOperationException>(() => builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTestContext>(o =>
        {
            o.AddTargetSystem(System, 200);
            o.AddCampaign<TestTarget>("config-push", c => c.MaxTargets(10).TargetSystem("Device-Core").Targets((_, _) => Targets(1)));
        }));
        Assert.Contains($"did you mean '{System}'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDeclarationMayFollowTheType_AndTheHostWiresIt()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTestContext>(o =>
        {
            o.AddCampaign<TestTarget>("config-push", c => c.MaxTargets(10).TargetSystem(System).Targets((_, _) => Targets(1)));
            o.AddTargetSystem(System, 200);   // after the type: the check runs once the whole delegate has
        });
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<GoldpathCampaignOptions>();
        Assert.Equal(200, options.TargetSystems[System]);
        Assert.Equal(System, options.Types.Single(t => t.Key == "config-push").TargetSystem);
    }

    // ---- the seed rows (owner decision Q4) ----

    [Fact]
    public async Task TheLeaderSeedsOneRowPerSystem_AndTheGlobalRow()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200, o => o.GlobalTps = 1_000));
        await ReconcileAsync(fixture);

        var rows = Rows(fixture);
        Assert.Equal(2, rows.Count);
        var global = rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.Global);
        Assert.Equal(GoldpathCampaignCeiling.GlobalName, global.Name);
        Assert.Equal(1_000, global.MaxTps);
        Assert.Equal(1_000, global.SeedMaxTps);
        var system = rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((System, 200, 200, "system"), (system.Name, system.MaxTps, system.SeedMaxTps, system.UpdatedBy));

        await ReconcileAsync(fixture);   // a second slice changes nothing
        Assert.Equal(2, Rows(fixture).Count);
    }

    [Fact]
    public async Task WithGlobalTpsOff_TheGlobalRowIsSeededOff()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        await ReconcileAsync(fixture);
        var global = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.Global);
        Assert.Null(global.MaxTps);
        Assert.Null(global.SeedMaxTps);
    }

    [Fact]
    public async Task AChangedCodeValueIsFollowed_WhileTheOperatorNeverMovedTheRow()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200, o => o.GlobalTps = 1_000));
        await ReconcileAsync(fixture);

        // The next release seeds 300 and 800: nobody moved the rows, so both follow code.
        await ReconcileAsync(fixture, Reseeded(fixture.Options, systemTps: 300, globalTps: 800));
        var system = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((300, 300), (system.MaxTps, system.SeedMaxTps));
        var global = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.Global);
        Assert.Equal((800, 800), (global.MaxTps, global.SeedMaxTps));
    }

    [Fact]
    public async Task AChangedCodeValueIsNotFollowed_OnceTheOperatorMovedTheRow_AndTheDisagreementIsLogged()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        await ReconcileAsync(fixture);
        MoveSystemRow(fixture, 100, "ops@example.test");   // tonight it takes a hundred

        var logger = new ListLogger<GoldpathCampaignEngine<CampaignTestContext>>();
        await ReconcileAsync(fixture, Reseeded(fixture.Options, systemTps: 300, globalTps: null), logger);

        var system = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((100, 200, "ops@example.test"), (system.MaxTps, system.SeedMaxTps, system.UpdatedBy));
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(System, warning.Message, StringComparison.Ordinal);
        Assert.Contains("300", warning.Message, StringComparison.Ordinal);
        Assert.Contains("100", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOperatorWhoSetTheSeedValueBack_HandsTheRowBackToCode()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        await ReconcileAsync(fixture);
        MoveSystemRow(fixture, 200, "ops@example.test");   // moved, then moved back to exactly the seed

        await ReconcileAsync(fixture, Reseeded(fixture.Options, systemTps: 300, globalTps: null));
        Assert.Equal(300, Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem).MaxTps);
    }

    [Theory]
    // The admin verb wrote the row first (never seeded): the operator's value stays.
    [InlineData("7", "NULL", "ops@example.test", 7, "ops@example.test")]
    // Another leader seeded it first from an older release (7, unmoved): the re-read follows this code, 200.
    [InlineData("7", "7", "system", 200, "system")]
    public async Task AnInsertThatMeetsAConcurrentRow_ReReadsIt_AndAppliesTheSeedRuleToIt(
        string liveSql, string seedSql, string by, int expectedTps, string expectedBy)
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        // Between the leader's read and its first insert, the other writer's row for the system lands.
        var interceptor = new InsertFirst(
            $"INSERT INTO \"GoldpathCampaignCeilings\" (\"Kind\", \"Name\", \"MaxTps\", \"SeedMaxTps\", \"UpdatedAt\", \"UpdatedBy\") "
            + $"VALUES (1, '{System}', {liveSql}, {seedSql}, '2026-11-05 00:00:00+00:00', '{by}')");
        var options = new DbContextOptionsBuilder<CampaignTestContext>().UseSqlite(fixture.Connection).AddInterceptors(interceptor).Options;
        await using (var db = new CampaignTestContext(options))
        {
            await fixture.Engine.ReconcileCeilingsAsync(db, CancellationToken.None);   // no exception escapes the conflict
        }

        Assert.True(interceptor.Fired);
        var rows = Rows(fixture);
        var system = Assert.Single(rows, r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((expectedTps, expectedBy), (system.MaxTps, system.UpdatedBy));
        Assert.Single(rows, r => r.Kind == GoldpathCampaignCeilingKind.Global);   // the other row still landed
    }

    [Fact]
    public async Task ASeedRowRefusedForAnotherReasonThanAConflict_IsLoggedAtWarning_WithItsException()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        // An adopter's DBA refuses every insert into the table (a missing grant reads the same way).
        await using (var deny = fixture.Connection.CreateCommand())
        {
            deny.CommandText = "CREATE TRIGGER deny_ceiling_insert BEFORE INSERT ON \"GoldpathCampaignCeilings\" "
                + "BEGIN SELECT RAISE(ABORT, 'permission denied for table GoldpathCampaignCeilings'); END;";
            await deny.ExecuteNonQueryAsync();
        }

        var logger = new ListLogger<GoldpathCampaignEngine<CampaignTestContext>>();
        await ReconcileAsync(fixture, fixture.Options, logger);   // fails safe: nothing escapes, the code seed binds

        Assert.Empty(Rows(fixture));
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);   // the global row and the system's, each named once
        var system = Assert.Single(warnings, w => w.Message.Contains($"'{System}'", StringComparison.Ordinal));
        Assert.Contains("code seed binds", system.Message, StringComparison.Ordinal);
        Assert.Contains("permission denied", system.Message, StringComparison.Ordinal);
        Assert.IsType<DbUpdateException>(system.Exception);
        Assert.Single(warnings, w => w.Message.Contains($"'{GoldpathCampaignCeiling.GlobalName}'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnInsertThatMeetsAConcurrentRow_IsQuiet()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        var interceptor = new InsertFirst(
            $"INSERT INTO \"GoldpathCampaignCeilings\" (\"Kind\", \"Name\", \"MaxTps\", \"SeedMaxTps\", \"UpdatedAt\", \"UpdatedBy\") "
            + $"VALUES (1, '{System}', 200, 200, '2026-11-05 00:00:00+00:00', 'system')");
        var options = new DbContextOptionsBuilder<CampaignTestContext>().UseSqlite(fixture.Connection).AddInterceptors(interceptor).Options;
        var logger = new ListLogger<GoldpathCampaignEngine<CampaignTestContext>>();
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(fixture.Options, TimeProvider.System, logger);
        await using (var db = new CampaignTestContext(options))
        {
            await engine.ReconcileCeilingsAsync(db, CancellationToken.None);
        }

        Assert.True(interceptor.Fired);
        Assert.Equal(2, Rows(fixture).Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AFollowIsGuardedOnTheValuesItRead_SoAnOperatorsChangeInBetweenStands()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        await ReconcileAsync(fixture);
        // The leader read (200, 200); before its update lands, an operator writes 50.
        var interceptor = new BeforeUpdate($"UPDATE \"GoldpathCampaignCeilings\" SET \"MaxTps\" = 50, \"UpdatedBy\" = 'ops@example.test' WHERE \"Kind\" = 1");
        var options = new DbContextOptionsBuilder<CampaignTestContext>().UseSqlite(fixture.Connection).AddInterceptors(interceptor).Options;
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(Reseeded(fixture.Options, systemTps: 300, globalTps: null),
            TimeProvider.System, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        await using (var db = new CampaignTestContext(options))
        {
            await engine.ReconcileCeilingsAsync(db, CancellationToken.None);
        }

        Assert.True(interceptor.Fired);
        var system = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((50, 200, "ops@example.test"), (system.MaxTps, system.SeedMaxTps, system.UpdatedBy));
    }

    [Fact]
    public async Task OfTwoSystemsSeededAlike_OnlyTheOneWhoseCodeMoved_Follows()
    {
        using var fixture = new CampaignFixture(FastTicks(o =>
        {
            o.AddTargetSystem("sms-gateway", 200);
            o.AddTargetSystem("device-core", 200);
        }));
        await ReconcileAsync(fixture);

        var next = new GoldpathCampaignOptions();
        next.AddTargetSystem("sms-gateway", 300);   // only this one's code moved
        next.AddTargetSystem("device-core", 200);
        await ReconcileAsync(fixture, next);

        var rows = Rows(fixture).Where(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem).ToDictionary(r => r.Name, r => (r.MaxTps, r.SeedMaxTps));
        Assert.Equal((300, 300), rows["sms-gateway"]);
        Assert.Equal((200, 200), rows["device-core"]);
    }

    [Fact]
    public async Task ACodeValueThatDidNotMove_LeavesAnOperatorsRowAlone_WithoutAWarning()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200));
        await ReconcileAsync(fixture);
        MoveSystemRow(fixture, 100, "ops@example.test");

        var logger = new ListLogger<GoldpathCampaignEngine<CampaignTestContext>>();
        await ReconcileAsync(fixture, fixture.Options, logger);   // the same code: nothing to follow, nothing to disagree with

        var system = Rows(fixture).Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem);
        Assert.Equal((100, 200, "ops@example.test"), (system.MaxTps, system.SeedMaxTps, system.UpdatedBy));
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task WithNoRowsYet_TheLiveCeilingsAreTheCodeSeeds()
    {
        // A row deleted since the slice began (or a read before the first reconcile) never opens a ceiling.
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(200, o => o.GlobalTps = 900));
        using var scope = fixture.Services.CreateScope();
        var live = await fixture.Engine.ReadCeilingsAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), CancellationToken.None);

        Assert.Equal(900, live.GlobalTps);
        Assert.Equal(200, live.TargetSystems[System]);
    }

    [Fact]
    public async Task ASystemNamedLikeTheGlobalRow_IsStillItsOwnRow()
    {
        // The key is (Kind, Name): a system called "*" and the global row "*" are two rows, read apart.
        using var fixture = new CampaignFixture(FastTicks(o =>
        {
            o.GlobalTps = 900;
            o.AddTargetSystem(GoldpathCampaignCeiling.GlobalName, 7);
        }));
        await ReconcileAsync(fixture);

        var rows = Rows(fixture);
        Assert.Equal(2, rows.Count);
        Assert.Equal(900, rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.Global).MaxTps);
        Assert.Equal(7, rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem).MaxTps);

        using var scope = fixture.Services.CreateScope();
        var live = await fixture.Engine.ReadCeilingsAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), CancellationToken.None);
        Assert.Equal(900, live.GlobalTps);
        Assert.Equal(7, live.TargetSystems[GoldpathCampaignCeiling.GlobalName]);

        // A later release moves only the system's code: the system's row follows, the global row is not mistaken for it.
        var next = new GoldpathCampaignOptions { GlobalTps = 900 };
        next.AddTargetSystem(GoldpathCampaignCeiling.GlobalName, 8);
        await ReconcileAsync(fixture, next);
        rows = Rows(fixture);
        Assert.Equal((8, 8), (rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem).MaxTps, rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.TargetSystem).SeedMaxTps));
        Assert.Equal(900, rows.Single(r => r.Kind == GoldpathCampaignCeilingKind.Global).MaxTps);
    }

    // ---- the pacer's target-system bucket ----

    [Fact]
    public async Task TwoTypesOnOneSystem_ShareItsBucket_EvenlyWhenEqual_AndNeverJointlyExceedIt()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(500));
        var (a, b) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        await fixture.RunPacerSliceAsync();

        var releasedA = fixture.Reload(a).ReleasedThrough;
        var releasedB = fixture.Reload(b).ReleasedThrough;
        var total = releasedA + releasedB;
        // 500/s over a 0.4 s slice is 200, plus at most one tick's bank (10 at 20 ms, #292): never the 2 × 10,000 their own Tps allows.
        Assert.InRange(total, 20, 210);
        Assert.InRange(releasedA, total * 0.4, total * 0.6);
    }

    [Fact]
    public async Task TwoTypesOnOneSystem_UnderPriority_HighOutpacesLow_ByItsWeight()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(500));
        var (low, high) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Low), ("firmware-push", GoldpathCampaignPriority.High));
        await fixture.RunPacerSliceAsync();

        var releasedLow = fixture.Reload(low).ReleasedThrough;
        var releasedHigh = fixture.Reload(high).ReleasedThrough;
        Assert.True(releasedLow > 0, "the Low campaign must still move");
        // R2.5's weights are 3:1 for High over Low; allow the rounding a short slice leaves.
        Assert.True(releasedHigh >= releasedLow * 2, $"High released {releasedHigh}, Low {releasedLow} — expected about 3:1");
        Assert.InRange(releasedLow + releasedHigh, 20, 210);
    }

    [Fact]
    public async Task ATypeNamingNoSystem_KeepsItsR2Bucket_AndIsNotChargedForTheSystem()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(20, o =>
            o.AddCampaign<TestTarget>("bulk", c => c.MaxTargets(10_000).MaxTps(500).Targets((_, _) => Targets(4_000)))));
        var (onSystem, bulk) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("bulk", GoldpathCampaignPriority.Normal));
        await fixture.RunPacerSliceAsync();

        // The device core's 20/s binds config-push; bulk runs at its own type ceiling, 500/s, untouched by it.
        Assert.InRange(fixture.Reload(onSystem).ReleasedThrough, 1, 9);
        Assert.InRange(fixture.Reload(bulk).ReleasedThrough, 50, 210);
    }

    [Fact]
    public async Task TheBucketsNest_ATypeCeilingUnderTheSystemStillBinds()
    {
        using var fixture = new CampaignFixture(FastTicks(o =>
        {
            o.AddTargetSystem(System, 500);
            o.AddCampaign<TestTarget>("config-push", c => c.MaxTargets(10_000).MaxTps(50).TargetSystem(System).Targets((_, _) => Targets(4_000)));
            o.AddCampaign<TestTarget>("firmware-push", c => c.MaxTargets(10_000).TargetSystem(System).Targets((_, _) => Targets(4_000)));
        }));
        var (capped, open) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        await fixture.RunPacerSliceAsync();

        var releasedCapped = fixture.Reload(capped).ReleasedThrough;
        var releasedOpen = fixture.Reload(open).ReleasedThrough;
        Assert.InRange(releasedCapped, 1, 21);             // its type's 50/s over 0.4 s, plus one tick's bank
        Assert.InRange(releasedCapped + releasedOpen, 20, 210);   // and the system's 500/s over both
        Assert.True(releasedOpen > releasedCapped * 3, $"the uncapped type should take what the capped one cannot: {releasedOpen} vs {releasedCapped}");
    }

    [Fact]
    public async Task ARowChange_BitesOnTheNextTick_WithNoRestart()
    {
        // The change is made from inside the slice, on the pacer's own thread, once 100 items have gone out: the fixture's
        // in-memory SQLite connection is shared and not safe for a second thread, so an operator "in between" is modelled
        // as a write the publish path makes — after it, the rest of the slice must run at the new value.
        CampaignFixture? owner = null;
        var publisher = new ChangingPublisher(after: 100, () => MoveSystemRow(owner!, 5, "ops@example.test"));
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(2_000, sliceMs: 1_500),
            extraServices: services => services.AddSingleton<IIntegrationEventPublisher>(publisher));
        owner = fixture;
        await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        await fixture.RunPacerSliceAsync();

        Assert.True(publisher.ChangedAt is not null, $"the slice released only {publisher.Count} — the change was never made");
        // At 2,000/s the remaining second and more would release over a thousand; at 5/s the rest of the batch the change
        // landed in (at most one tick's 40), five a second for at most 1.5 s, and one tick's bank.
        var afterChange = publisher.Count - publisher.ChangedAt!.Value;
        Assert.InRange(afterChange, 0, 60);
    }

    [Fact]
    public async Task AGlobalCeilingSwitchedOnMidSlice_StartsEmpty_NotInDebtForWhatWentOutWhileItWasOff()
    {
        // Off, the global ceiling charges nothing: 2,800 items go out, then an operator turns it on at 50/s. A bucket that
        // had been charged while off would start 2,800 in debt and, at one unit a tick, release nothing for a minute.
        // Each campaign's own Tps (10,000/s) gives exactly 200 a 20 ms tick, so 2,800 is the last item of the seventh
        // tick: everything counted after the change went out on a tick that read the global row.
        CampaignFixture? owner = null;
        var publisher = new ChangingPublisher(after: 2_800, () => owner!.Mutate(db =>
        {
            var global = db.Set<GoldpathCampaignCeiling>().Single(c => c.Kind == GoldpathCampaignCeilingKind.Global);
            global.MaxTps = 50;
            global.UpdatedBy = "ops@example.test";
        }));
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(1_000_000, sliceMs: 1_500),
            extraServices: services => services.AddSingleton<IIntegrationEventPublisher>(publisher));
        owner = fixture;
        await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        await fixture.RunPacerSliceAsync();

        Assert.True(publisher.ChangedAt is not null, $"the slice released only {publisher.Count} — the change was never made");
        var afterChange = publisher.Count - publisher.ChangedAt!.Value;
        // 50/s is one a tick at 20 ms, and the slice has up to 68 ticks left: some, never zero, and never more than that.
        Assert.InRange(afterChange, 5, 70);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public async Task ARowBelowOne_ReleasesNothingForThatSystem(int rowValue)
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(500));
        var (a, b) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        await ReconcileAsync(fixture);
        MoveSystemRow(fixture, rowValue, "ops@example.test");   // only an edit outside the module can write this
        await fixture.RunPacerSliceAsync();
        Assert.Equal(0, fixture.Reload(a).ReleasedThrough + fixture.Reload(b).ReleasedThrough);

        using var scope = fixture.Services.CreateScope();
        var live = await fixture.Engine.ReadCeilingsAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), CancellationToken.None);
        Assert.Equal(0, live.TargetSystems[System]);
    }

    [Fact]
    public async Task TheGlobalCeilingIsReadFromItsRow()
    {
        // Code seeds GlobalTps 10,000; the operator's row says 20/s, and that is what binds a campaign with no other ceiling.
        using var fixture = new CampaignFixture(FastTicks(o => o.GlobalTps = 10_000));
        await ReconcileAsync(fixture);
        fixture.Mutate(db =>
        {
            var global = db.Set<GoldpathCampaignCeiling>().Single(c => c.Kind == GoldpathCampaignCeilingKind.Global);
            global.MaxTps = 20;
            global.UpdatedBy = "ops@example.test";
        });
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();

        Assert.InRange(fixture.Reload(campaign.Id).ReleasedThrough, 1, 9);   // the winback source holds 10; 20/s for 0.4 s is 8
    }

    [Fact]
    public async Task TheDetailNamesTheTargetSystemThatBindsIt()
    {
        using var fixture = new CampaignFixture(TwoTypesOnOneSystem(500));
        var (onSystem, _) = await CreateTwoAsync(fixture, ("config-push", GoldpathCampaignPriority.Normal), ("firmware-push", GoldpathCampaignPriority.Normal));
        var plain = await fixture.CreateAsync(Generous());

        var admin = fixture.Admin();
        Assert.Equal(System, (await admin.GetAsync(onSystem, CancellationToken.None))!.TargetSystem);
        Assert.Null((await admin.GetAsync(plain.Id, CancellationToken.None))!.TargetSystem);
    }

    // ---- helpers ----

    private static async Task ReconcileAsync(
        CampaignFixture fixture, GoldpathCampaignOptions? options = null, ILogger<GoldpathCampaignEngine<CampaignTestContext>>? logger = null)
    {
        var engine = options is null && logger is null
            ? fixture.Engine
            : new GoldpathCampaignEngine<CampaignTestContext>(options ?? fixture.Options, TimeProvider.System,
                logger ?? NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        using var scope = fixture.Services.CreateScope();
        await engine.ReconcileCeilingsAsync(scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), CancellationToken.None);
    }

    /// <summary>The same types as <paramref name="current"/>, with the code values of the next release.</summary>
    private static GoldpathCampaignOptions Reseeded(GoldpathCampaignOptions current, int systemTps, int? globalTps)
    {
        var next = new GoldpathCampaignOptions { GlobalTps = globalTps };
        foreach (var name in current.TargetSystems.Keys)
        {
            next.AddTargetSystem(name, systemTps);
        }

        return next;
    }

    private static List<GoldpathCampaignCeiling> Rows(CampaignFixture fixture)
        => fixture.Query(db => db.Set<GoldpathCampaignCeiling>().AsNoTracking().ToList());

    private static void MoveSystemRow(CampaignFixture fixture, int maxTps, string actor)
        => fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathCampaignCeiling>().Single(c => c.Kind == GoldpathCampaignCeilingKind.TargetSystem && c.Name == System);
            row.MaxTps = maxTps;
            row.UpdatedBy = actor;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        });

    private static async Task<(Guid First, Guid Second)> CreateTwoAsync(
        CampaignFixture fixture, (string Type, GoldpathCampaignPriority Priority) first, (string Type, GoldpathCampaignPriority Priority) second)
    {
        using var scope = fixture.Services.CreateScope();
        var a = await fixture.Engine.CreateAsync(scope.ServiceProvider, first.Type, "first",
            new Dictionary<string, string>(), Generous(first.Priority), tenant: null, actor: "tester", CancellationToken.None);
        var b = await fixture.Engine.CreateAsync(scope.ServiceProvider, second.Type, "second",
            new Dictionary<string, string>(), Generous(second.Priority), tenant: null, actor: "tester", CancellationToken.None);
        // Enumerated BEFORE the slice, as R2PacingTests does: the assertions are about release shares.
        await fixture.EnumerateAllAsync(a);
        await fixture.EnumerateAllAsync(b);
        return (a.Id, b.Id);
    }

    private static async IAsyncEnumerable<TestTarget> Targets(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new TestTarget(i, $"device{i}@example.test");
        }

        await Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="sql"/> once, just before the first INSERT into the ceilings table: a concurrent writer winning the key.</summary>
    private sealed class InsertFirst(string sql) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(command, "INSERT INTO \"GoldpathCampaignCeilings\"", cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await RunOnceAsync(command, "INSERT INTO \"GoldpathCampaignCeilings\"", cancellationToken);
            return result;
        }

        private async Task RunOnceAsync(DbCommand command, string trigger, CancellationToken cancellationToken)
        {
            if (Fired || !command.CommandText.Contains(trigger, StringComparison.Ordinal))
            {
                return;
            }

            Fired = true;
            await using var concurrent = command.Connection!.CreateCommand();
            concurrent.Transaction = command.Transaction;
            concurrent.CommandText = sql;
            await concurrent.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>Runs <paramref name="sql"/> once, just before the first UPDATE of the ceilings table: an operator's write landing in between.</summary>
    private sealed class BeforeUpdate(string sql) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("UPDATE \"GoldpathCampaignCeilings\"", StringComparison.Ordinal))
            {
                Fired = true;
                await using var concurrent = command.Connection!.CreateCommand();
                concurrent.Transaction = command.Transaction;
                concurrent.CommandText = sql;
                await concurrent.ExecuteNonQueryAsync(cancellationToken);
            }

            return result;
        }
    }

    /// <summary>Counts publishes and, once <paramref name="after"/> have gone out, runs <paramref name="change"/> once on the publishing thread.</summary>
    private sealed class ChangingPublisher(int after, Action change) : IIntegrationEventPublisher
    {
        public int Count { get; private set; }

        public int? ChangedAt { get; private set; }

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Count++;
            if (ChangedAt is null && Count >= after)
            {
                change();
                ChangedAt = Count;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
