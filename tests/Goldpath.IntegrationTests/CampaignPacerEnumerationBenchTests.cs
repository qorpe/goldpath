using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// #288: the snapshot measured on the path an adopter's campaign takes — through the pacer —
/// rather than the engine loop <see cref="CampaignBenchTests"/> measures. Trait-gated evidence
/// (scripts/bench-campaign.sh enum → ops/campaign-benchmarks.md). It runs in the non-parallel
/// bench collection: a multi-minute write load beside another bench would move both numbers.
/// </summary>
[Trait("Category", "Bench")]
[Collection(CampaignBrokerBenchCollection.Name)]
public sealed class CampaignPacerEnumerationBenchTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly ITestOutputHelper _output;
    private ServiceProvider _services = null!;
    private GoldpathCampaignOptions _options = null!;
    private GoldpathCampaignEngine<CampaignTests.CampDb> _engine = null!;

    public CampaignPacerEnumerationBenchTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new GoldpathCampaignOptions();   // the package defaults: 250 ms tick, 60 s slice, 2,000-row batch
        _options.AddCampaign<CampaignBenchTests.BenchTarget>("bench", c => c
            .MaxTargets(50_000_000)
            .Targets((_, parameters) => Synthetic(long.Parse(parameters["count"], CultureInfo.InvariantCulture))));

        _engine = new GoldpathCampaignEngine<CampaignTests.CampDb>(
            _options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<CampaignTests.CampDb>>.Instance);
        var services = new ServiceCollection();
        services.AddDbContext<CampaignTests.CampDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_options);
        services.AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher());
        services.AddSingleton(_engine);
        services.AddLogging();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<GoldpathCampaign> ReloadAsync(Guid id)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CampaignTests.CampDb>()
            .Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private static async IAsyncEnumerable<CampaignBenchTests.BenchTarget> Synthetic(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new CampaignBenchTests.BenchTarget(i);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// #288 / S-FLEET-02's enumeration leg: the snapshot as an adopter's campaign takes it —
    /// THROUGH THE PACER, at the package defaults (250 ms tick, 60 s slice, 2,000-row batch),
    /// slices run back to back (the cron's gap between them is not modelled). Size from
    /// GOLDPATH_BENCH_CAMPAIGN_ENUM_COUNT (default 1M). Reports wall time, the item table's
    /// heap and index size, and the leader's peak memory (sampled every 100 ms).
    /// </summary>
    [Fact]
    public async Task Enumeration_through_the_pacer_at_the_package_defaults()
    {
        var count = long.Parse(Environment.GetEnvironmentVariable("GOLDPATH_BENCH_CAMPAIGN_ENUM_COUNT") ?? "1000000",
            CultureInfo.InvariantCulture);
        using var scope = _services.CreateScope();
        var campaign = await _engine.CreateAsync(scope.ServiceProvider, "bench", "enum-pacer",
            new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) },
            new GoldpathCampaignPolicy(1, null, 1, null, null, "UTC"), null, "bench", CancellationToken.None);
        var pacer = new GoldpathCampaignPacerJob<CampaignTests.CampDb>(
            _engine, _options, TimeProvider.System, NullLogger<GoldpathCampaignPacerJob<CampaignTests.CampDb>>.Instance);
        var context = CampaignFixtureLike.CreateContext(scope.ServiceProvider);

        GC.Collect();
        var managedPeak = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        TimeSpan? completedAt = null;   // the slice leads on after the flip; the clock stops AT the flip
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            var polls = 0;
            while (!sampling.IsCancellationRequested)
            {
                managedPeak = Math.Max(managedPeak, GC.GetTotalMemory(forceFullCollection: false));
                if (completedAt is null && ++polls % 5 == 0 && (await ReloadAsync(campaign.Id)).EnumerationComplete)
                {
                    completedAt = watch.Elapsed;
                }

                try
                {
                    await Task.Delay(100, sampling.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var slices = 0;
        GoldpathCampaign row;
        do
        {
            await pacer.ExecuteChunkAsync(CampaignFixtureLike.MakeChunk(0, "lead"), context, CancellationToken.None);
            slices++;
            row = await ReloadAsync(campaign.Id);
            _output.WriteLine($"BENCH-CAMPAIGN pacer-enum: slice {slices} at {watch.Elapsed.TotalSeconds:F0} s, {row.EnumeratedThrough:N0} enumerated");
        }
        while (!row.EnumerationComplete);

        await sampling.CancelAsync();
        await sampler;
        var elapsed = completedAt ?? watch.Elapsed;

        long heap, indexes;
        await using (var connection = new Npgsql.NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand(
                "SELECT pg_table_size('\"GoldpathCampaignItems\"'), pg_indexes_size('\"GoldpathCampaignItems\"')", connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            heap = reader.GetInt64(0);
            indexes = reader.GetInt64(1);
        }

        var process = Process.GetCurrentProcess();
        _output.WriteLine(
            $"BENCH-CAMPAIGN pacer-enum: {count:N0} targets through the pacer in {elapsed.TotalSeconds:F1} s "
            + $"= {row.EnumeratedThrough / elapsed.TotalSeconds:F0} rows/s over {slices} slices; "
            + $"items table {heap / 1048576.0:F0} MiB + indexes {indexes / 1048576.0:F0} MiB "
            + $"({(heap + indexes) / (double)count:F0} B/item); leader managed-heap peak {managedPeak / 1048576.0:F0} MiB, "
            + $"process peak working set {process.PeakWorkingSet64 / 1048576.0:F0} MiB");
        Assert.Equal(count, row.EnumeratedThrough);
    }
}
