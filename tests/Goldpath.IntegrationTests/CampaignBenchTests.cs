using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// Campaign RFC §6 performance proofs (scripts/bench-campaign.sh →
/// ops/campaign-benchmarks.md). Trait-gated: evidence, not regression tests.
/// The pacer's PRECISION is the product here — a governor that cannot hold its
/// ceiling is not a governor. Written once: PostgreSQL here, Oracle in <see cref="CampaignBenchOracleTests"/> (#557);
/// <c>scripts/bench-campaign.sh</c> picks one with <c>GOLDPATH_BENCH_PROVIDER</c>.
/// </summary>
[Trait("Category", "Bench")]
public class CampaignBenchTests : IAsyncLifetime
{
    public sealed record BenchTarget(long Id);

    /// <summary>Counts publishes; the wire is not what this bench measures.</summary>
    internal sealed class CountingPublisher : IIntegrationEventPublisher
    {
        public long Published;

        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Interlocked.Increment(ref Published);
            return Task.CompletedTask;
        }
    }

    private readonly SuiteStore _store;
    private readonly ITestOutputHelper _output;
    private ServiceProvider _services = null!;
    private GoldpathCampaignOptions _options = null!;
    private CountingPublisher _publisher = null!;

    /// <summary>The PostgreSQL bench: a container of its own, as before #557.</summary>
    public CampaignBenchTests(ITestOutputHelper output)
        : this(SuiteStore.PostgreSql(), output)
    {
    }

    /// <summary>The same bench on <paramref name="store"/>.</summary>
    protected CampaignBenchTests(SuiteStore store, ITestOutputHelper output)
    {
        _store = store;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        await _store.StartAsync();
        _options = new GoldpathCampaignOptions();
        _options.AddCampaign<BenchTarget>("bench", c => c
            .MaxTargets(2_000_000)
            .Targets((_, parameters) => Synthetic(long.Parse(parameters["count"]))));

        _publisher = new CountingPublisher();
        var services = new ServiceCollection();
        services.AddDbContext<CampaignTests.CampDb>(_store.Use);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_options);
        services.AddSingleton<IIntegrationEventPublisher>(_publisher);
        services.AddSingleton(new GoldpathCampaignEngine<CampaignTests.CampDb>(
            _options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<CampaignTests.CampDb>>.Instance));
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _store.StopAsync();
    }

    private GoldpathCampaignEngine<CampaignTests.CampDb> Engine
        => _services.GetRequiredService<GoldpathCampaignEngine<CampaignTests.CampDb>>();

    [Fact]
    public async Task Enumeration_materializes_one_million_targets()
    {
        using var scope = _services.CreateScope();
        var campaign = await Engine.CreateAsync(scope.ServiceProvider, "bench", "enum-1m",
            new Dictionary<string, string> { ["count"] = "1000000" },
            new GoldpathCampaignPolicy(1, null, 1, null, null, "UTC"), null, "bench", CancellationToken.None);

        var watch = Stopwatch.StartNew();
        var stream = await Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
        try
        {
            while (!campaign.EnumerationComplete)
            {
                await Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        watch.Stop();
        var rate = campaign.EnumeratedThrough / watch.Elapsed.TotalSeconds;
        _output.WriteLine($"BENCH-CAMPAIGN enumeration: 1,000,000 targets materialized in {watch.Elapsed.TotalSeconds:F1} s = {rate:F0} rows/s{BenchProvider.Tag(_store)}");
        Assert.Equal(1_000_000, campaign.EnumeratedThrough);
    }

    [Fact]
    public async Task Pacer_holds_200_tps_and_a_mid_run_throttle_bites_within_a_tick()
    {
        _options.EnumerationBatchSize = 5_000;
        _options.LeaderTick = TimeSpan.FromMilliseconds(250);
        _options.LeadershipSlice = TimeSpan.FromSeconds(15);

        using var scope = _services.CreateScope();
        var campaign = await Engine.CreateAsync(scope.ServiceProvider, "bench", "pacer-precision",
            new Dictionary<string, string> { ["count"] = "20000" },
            new GoldpathCampaignPolicy(200, null, 1_000_000, null, null, "UTC"), null, "bench", CancellationToken.None);

        var pacer = new GoldpathCampaignPacerJob<CampaignTests.CampDb>(
            Engine, _options, TimeProvider.System, NullLogger<GoldpathCampaignPacerJob<CampaignTests.CampDb>>.Instance);
        var context = CampaignFixtureLike.CreateContext(scope.ServiceProvider);

        // Phase 1: 15 s at 200 TPS.
        await pacer.ExecuteChunkAsync(CampaignFixtureLike.MakeChunk(0, "lead"), context, CancellationToken.None);
        var afterPhase1 = await ReloadAsync(campaign.Id);
        var phase1Rate = afterPhase1.ReleasedThrough / 15.0;
        _output.WriteLine($"BENCH-CAMPAIGN pacer: configured 200 TPS, measured {phase1Rate:F1} released/s over 15 s ({afterPhase1.ReleasedThrough} items){BenchProvider.Tag(_store)}");

        // Phase 2: LIVE throttle to 50 on the row; next slice must hold the NEW ceiling.
        using (var throttleScope = _services.CreateScope())
        {
            await throttleScope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>()
                .Set<GoldpathCampaign>().Where(c => c.Id == campaign.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Tps, 50), CancellationToken.None);
        }

        var beforePhase2 = afterPhase1.ReleasedThrough;
        await pacer.ExecuteChunkAsync(CampaignFixtureLike.MakeChunk(0, "lead"), context, CancellationToken.None);
        var afterPhase2 = await ReloadAsync(campaign.Id);
        var phase2Rate = (afterPhase2.ReleasedThrough - beforePhase2) / 15.0;
        _output.WriteLine($"BENCH-CAMPAIGN throttle: live-throttled to 50 TPS, measured {phase2Rate:F1} released/s over the next 15 s{BenchProvider.Tag(_store)}");

        // #557: on Oracle the numbers are measured, not promised (oracle RFC §6), so the precision band binds PostgreSQL
        // only. That the live throttle BIT is still asserted: a slower second phase than the first.
        if (_store.IsOracle)
        {
            Assert.True(phase2Rate < phase1Rate, $"the live throttle did not bite: {phase1Rate:F1}/s then {phase2Rate:F1}/s");
            return;
        }

        Assert.InRange(phase1Rate, 180, 210);   // ±10% on the loose side; the report carries the real number
        Assert.InRange(phase2Rate, 40, 60);
    }

    [Fact]
    public async Task Sink_flushes_one_hundred_thousand_outcomes_batched()
    {
        _options.EnumerationBatchSize = 5_000;
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>();
        var campaign = await Engine.CreateAsync(scope.ServiceProvider, "bench", "sink-100k",
            new Dictionary<string, string> { ["count"] = "100000" },
            new GoldpathCampaignPolicy(1_000_000, null, 1_000_000, null, null, "UTC"), null, "bench", CancellationToken.None);
        var stream = await Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
        try
        {
            while (!campaign.EnumerationComplete)
            {
                await Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
            }
        }
        finally
        {
            await stream.DisposeAsync();
        }

        while (await Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1_000_000, CancellationToken.None) > 0)
        {
        }

        // Claim everything set-based (the consumers' work, condensed for the bench).
        await db.Set<GoldpathCampaignItem>().Where(i => i.CampaignId == campaign.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Processing)
                .SetProperty(i => i.ClaimedAt, DateTimeOffset.UtcNow), CancellationToken.None);

        var watch = Stopwatch.StartNew();
        for (long from = 1; from <= 100_000; from += 200)
        {
            var batch = Enumerable.Range(0, 200)
                .Select(offset => new GoldpathCampaignOutcomeMessage(campaign.Id, from + offset, true, null))
                .ToList();
            await Engine.ApplyOutcomesAsync(db, campaign.Id, batch, CancellationToken.None);
        }

        watch.Stop();
        var rate = 100_000 / watch.Elapsed.TotalSeconds;
        _output.WriteLine($"BENCH-CAMPAIGN sink: 100,000 outcomes flushed (batches of 200) in {watch.Elapsed.TotalSeconds:F1} s = {rate:F0} outcomes/s{BenchProvider.Tag(_store)}");
        var row = await ReloadAsync(campaign.Id);
        Assert.Equal(100_000, row.SucceededCount);
    }

    private async Task<GoldpathCampaign> ReloadAsync(Guid id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>()
            .Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async IAsyncEnumerable<BenchTarget> Synthetic(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new BenchTarget(i);
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="CampaignBenchTests"/> on Oracle (#557): a fresh schema on the Oracle collection's pinned container. Both
/// traits are on the class so the per-PR <c>oracle</c> lane (<c>Category=Oracle&amp;Category!=Bench</c>) never runs it.
/// </summary>
[Trait("Category", "Bench")]
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class CampaignBenchOracleTests(OracleFixture oracle, ITestOutputHelper output)
    : CampaignBenchTests(SuiteStore.Oracle(oracle, "GP_CBENCH"), output);

/// <summary>The unit fixture's reflection helpers, needed here too (internal jobs ctors).</summary>
internal static class CampaignFixtureLike
{
    internal static GoldpathJobContext CreateContext(IServiceProvider services)
        => (GoldpathJobContext)Activator.CreateInstance(typeof(GoldpathJobContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [Guid.NewGuid(), "bench", "node", "job", false, null, services], null)!;

    internal static GoldpathJobChunk MakeChunk(int index, string payload)
        => (GoldpathJobChunk)Activator.CreateInstance(typeof(GoldpathJobChunk),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [index, payload], null)!;
}
