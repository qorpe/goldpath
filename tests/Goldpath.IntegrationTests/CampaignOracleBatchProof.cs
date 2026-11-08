using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// G7 ORA-5 (#408; RFC goldpath-oracle §2.5): a campaign with <c>ReleaseBatchSize = 5000</c> on a real Oracle. Each
/// of the engine's three lists that <c>ReleaseBatchSize</c> bounds carries all 5,000 sequence numbers in one statement —
/// the orphan re-release (<c>orphans</c>), the ripe-retry release (<c>due</c>) and the success flush (<c>seqs</c>) —
/// and every item ends <c>Succeeded</c>. Oracle refuses an <c>IN</c> list above 1,000 (ORA-01795), so without
/// <see cref="GoldpathQueryableExtensions.ChunkedContains{T, TValue}"/> the first of the three fails.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class CampaignOracleBatchProof(OracleFixture oracle, ITestOutputHelper output)
{
    private const int Batch = 5_000;

    [Fact]
    public async Task A_5000_item_batch_releases_retries_and_settles_on_Oracle_with_every_item_terminal()
    {
        var connection = await oracle.CreateSchemaAsync("GP_CAMP_BATCH");
        var options = new GoldpathCampaignOptions
        {
            ReleaseBatchSize = Batch,
            EnumerationBatchSize = Batch,
            OrphanReleaseAfter = TimeSpan.Zero,   // every Released item is an orphan at once
            RetryBackoff = [TimeSpan.Zero],
            RetryJitter = 0,
        };
        options.AddCampaign<CampaignBenchTests.BenchTarget>("fleet", c => c.MaxTargets(Batch).Targets((_, _) => Targets(Batch)));
        var engine = new GoldpathCampaignEngine<OracleComposedDb>(options, TimeProvider.System, NullLogger<GoldpathCampaignEngine<OracleComposedDb>>.Instance);
        var services = new ServiceCollection()
            .AddScoped(_ => OracleComposedDb.Create(connection))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(options)
            .AddSingleton<IIntegrationEventPublisher>(new CampaignBenchTests.CountingPublisher())
            .AddSingleton(engine)
            .AddLogging()
            .BuildServiceProvider();
        await using (services)
        {
            using (var scope = services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<OracleComposedDb>().Database.EnsureCreatedAsync();
            }

            Guid id;
            using (var scope = services.CreateScope())
            {
                var campaign = await engine.CreateAsync(scope.ServiceProvider, "fleet", "batch proof", new Dictionary<string, string>(),
                    new GoldpathCampaignPolicy(Tps: 100_000, DailyQuota: null, MaxInFlight: Batch, WindowStart: null, WindowEnd: null, TimeZoneId: "UTC") { MaxAttempts = 2 },
                    tenant: null, actor: "it", CancellationToken.None);
                id = campaign.Id;
                await using var stream = await engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
                Assert.Equal(Batch, await engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None));
                Assert.Equal(0, await engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None));   // the end: Running
            }

            using (var scope = services.CreateScope())
            {
                Assert.Equal(GoldpathCampaignState.Running, (await LoadAsync(scope, id)).State);
                Assert.Equal(Batch, await engine.ReleaseBatchAsync(scope.ServiceProvider, await LoadAsync(scope, id), Batch, CancellationToken.None));
            }

            // orphans: all 5,000 Released items in one re-release statement.
            using (var scope = services.CreateScope())
            {
                Assert.Equal(Batch, await engine.ReleaseOrphansAsync(scope.ServiceProvider, await LoadAsync(scope, id), Batch, CancellationToken.None));
            }

            // Attempt 1 fails for every item: the ladder parks all 5,000 AwaitingRetry, ripe at once.
            await ClaimAllAsync(services, engine, id);
            await FlushAsync(services, engine, id, seq => new GoldpathCampaignOutcomeMessage(id, seq, false, "timed out") { ErrorCode = "TIMEOUT", Attempt = 1 });
            Assert.Equal([(GoldpathCampaignItemState.AwaitingRetry, Batch)], await HistogramAsync(services, id));

            // due: all 5,000 ripe retries in one release statement.
            using (var scope = services.CreateScope())
            {
                Assert.Equal(Batch, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, await LoadAsync(scope, id), Batch, CancellationToken.None));
            }

            // seqs: attempt 2 succeeds for every item, settled in one statement per prior state.
            await ClaimAllAsync(services, engine, id);
            await FlushAsync(services, engine, id, seq => new GoldpathCampaignOutcomeMessage(id, seq, true, null) { Attempt = 2 });

            var histogram = await HistogramAsync(services, id);
            output.WriteLine(string.Join(", ", histogram));
            Assert.Equal([(GoldpathCampaignItemState.Succeeded, Batch)], histogram);
        }

        using var pooled = new OracleConnection(connection);
        OracleConnection.ClearPool(pooled);   // the shared container's sessions are finite (ORA-12516)
    }

    private static async IAsyncEnumerable<CampaignBenchTests.BenchTarget> Targets(long count)
    {
        for (long i = 1; i <= count; i++)
        {
            yield return new CampaignBenchTests.BenchTarget(i);
        }

        await Task.CompletedTask;
    }

    private static Task<GoldpathCampaign> LoadAsync(IServiceScope scope, Guid id)
        => scope.ServiceProvider.GetRequiredService<OracleComposedDb>().Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == id);

    private static async Task ClaimAllAsync(ServiceProvider services, GoldpathCampaignEngine<OracleComposedDb> engine, Guid id)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OracleComposedDb>();
        for (long seq = 1; seq <= Batch; seq++)
        {
            Assert.NotNull(await engine.ClaimAsync(db, id, seq, CancellationToken.None));
        }
    }

    private static async Task FlushAsync(
        ServiceProvider services, GoldpathCampaignEngine<OracleComposedDb> engine, Guid id, Func<long, GoldpathCampaignOutcomeMessage> outcome)
    {
        using var scope = services.CreateScope();
        var outcomes = Enumerable.Range(1, Batch).Select(seq => outcome(seq)).ToList();
        await engine.ApplyOutcomesAsync(scope.ServiceProvider.GetRequiredService<OracleComposedDb>(), id, outcomes, CancellationToken.None);
    }

    private static async Task<List<(GoldpathCampaignItemState State, int Count)>> HistogramAsync(ServiceProvider services, Guid id)
    {
        using var scope = services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<OracleComposedDb>().Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == id).GroupBy(i => i.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        return [.. rows.OrderBy(r => r.Key).Select(r => (r.Key, r.Count))];
    }
}
