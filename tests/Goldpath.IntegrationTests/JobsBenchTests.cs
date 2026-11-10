using System.Diagnostics;
using Goldpath.Jobs.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The jobs RFC §6 performance proofs, runnable on demand (scripts/bench-jobs.sh — results
/// recorded in ops/jobs-benchmarks.md). Trait-gated out of the normal suite: benchmarks are
/// evidence, not regression tests. Written once: PostgreSQL here, Oracle in <see cref="JobsBenchOracleTests"/> (#556);
/// <c>scripts/bench-jobs.sh</c> picks one with <c>GOLDPATH_BENCH_PROVIDER</c>.
/// </summary>
[Trait("Category", "Bench")]
public class JobsBenchTests : IAsyncLifetime
{
    private readonly SuiteStore _store;

    /// <summary>The PostgreSQL bench: a container of its own, as before #556.</summary>
    public JobsBenchTests()
        : this(SuiteStore.PostgreSql())
    {
    }

    /// <summary>The same bench on <paramref name="store"/>.</summary>
    protected JobsBenchTests(SuiteStore store) => _store = store;

    private sealed class NoopJob : IGoldpathJob
    {
        public long Items { get; init; }
        public int ChunkSize { get; init; }
        public long Processed;

        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken ct)
            => Task.FromResult(GoldpathJobPlanner.ByRange(Items, ChunkSize));

        public Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken ct)
        {
            var (start, end) = GoldpathJobPlanner.ParseRange(chunk.Payload);
            for (var i = start; i < end; i++)
            {
                Interlocked.Increment(ref Processed);   // the no-op "work"
            }

            return Task.CompletedTask;
        }
    }

    public Task InitializeAsync() => _store.StartAsync<ClusterDb>(options => new(options));

    public Task DisposeAsync() => _store.StopAsync();

    [Fact]
    public async Task Bench_100k_items_checkpointed_vs_naked_loop()
    {
        const long items = 100_000;
        const int chunkSize = 500;

        var services = new ServiceCollection();
        services.AddDbContext<ClusterDb>(_store.Use);
        await using var provider = services.BuildServiceProvider();
        var runner = new GoldpathJobRunner<ClusterDb>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<GoldpathJobRunner<ClusterDb>>.Instance);

        // Naked baseline: same iteration, zero checkpointing.
        var naked = new NoopJob { Items = items, ChunkSize = chunkSize };
        var nakedWatch = Stopwatch.StartNew();
        var plan = await naked.PlanAsync(null!, CancellationToken.None);
        for (var index = 0; index < plan.ChunkPayloads.Count; index++)
        {
            await naked.ExecuteChunkAsync(new NoopJobChunkFactory().Create(index, plan.ChunkPayloads[index]), null!, CancellationToken.None);
        }

        nakedWatch.Stop();

        var checkpointed = new NoopJob { Items = items, ChunkSize = chunkSize };
        var options = new GoldpathJobsOptions();
        options.AddJob<NoopJob>(j => j.MaxParallelChunks = 4);
        var watch = Stopwatch.StartNew();
        var status = await runner.RunAsync(checkpointed, options.Jobs[0],
            new GoldpathFireFacts("bench", "bench-node", "bench-fire", false), CancellationToken.None);
        watch.Stop();

        Assert.Equal(GoldpathJobRunStatus.Completed, status);
        Assert.Equal(items, checkpointed.Processed);

        var overhead = nakedWatch.Elapsed > TimeSpan.Zero
            ? (watch.Elapsed - nakedWatch.Elapsed).TotalMilliseconds
            : 0;
        Console.WriteLine($"BENCH items={items} chunks={plan.ChunkPayloads.Count} " +
            $"checkpointed={watch.Elapsed.TotalSeconds:F1}s naked={nakedWatch.Elapsed.TotalSeconds:F1}s " +
            $"checkpoint-cost-per-chunk={overhead / plan.ChunkPayloads.Count:F1}ms{BenchProvider.Tag(_store)}");

        // #556: Oracle's numbers are measured, not promised (oracle RFC §6), so the budgets bind PostgreSQL only.
        if (_store.IsOracle)
        {
            return;
        }

        // RFC §6: plan→complete < 5 min on the reference profile.
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(5),
            $"100k run took {watch.Elapsed} — the §6 budget is 5 minutes");
        // Checkpoint overhead: < 25ms per chunk on the reference profile (≈<5% once chunks
        // do real work; a no-op loop makes PERCENTAGE meaningless, absolute cost honest).
        Assert.True(overhead / plan.ChunkPayloads.Count < 25,
            $"checkpoint cost {overhead / plan.ChunkPayloads.Count:F1}ms/chunk exceeds the 25ms budget");
    }

    [Fact]
    public async Task Bench_interactive_reads_under_a_full_tilt_job()
    {
        // The telco card's number at the layer where the contention actually lives (the
        // shared database): point-read p95 with NO job vs DURING a write-heavy run.
        var services = new ServiceCollection();
        services.AddDbContext<ClusterDb>(_store.Use);
        await using var provider = services.BuildServiceProvider();

        await using (var seed = new ClusterDb(_store.Options<ClusterDb>()))
        {
            for (var i = 0; i < 500; i++)
            {
                seed.Sink.Add(new SinkEntry { JobName = "seed", ChunkIndex = i, Instance = "seed" });
            }

            await seed.SaveChangesAsync();
        }

        async Task<double> InteractiveP95Async(CancellationToken ct)
        {
            var samples = new List<double>();
            for (var i = 0; i < 200 && !ct.IsCancellationRequested; i++)
            {
                await using var db = new ClusterDb(_store.Options<ClusterDb>());
                var start = Stopwatch.GetTimestamp();
                _ = await db.Sink.AsNoTracking().FirstOrDefaultAsync(s => s.ChunkIndex == i % 500 && s.JobName == "seed", ct);
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            samples.Sort();
            return samples[(int)(samples.Count * 0.95)];
        }

        var baseline = await InteractiveP95Async(CancellationToken.None);

        var runner = new GoldpathJobRunner<ClusterDb>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<GoldpathJobRunner<ClusterDb>>.Instance);
        var job = new WriteHeavyJob();
        var options = new GoldpathJobsOptions();
        options.AddJob<WriteHeavyJob>(j => { j.MaxParallelChunks = 4; j.Deadline = TimeSpan.FromMinutes(10); });
        using var runCts = new CancellationTokenSource();
        var run = runner.RunAsync(job, options.Jobs[0],
            new GoldpathFireFacts("bench-interactive", "bench-node", "bench-fire-2", false), runCts.Token);

        await Task.Delay(500);   // let the job reach full tilt
        var underLoad = await InteractiveP95Async(CancellationToken.None);
        runCts.Cancel();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // drained — the run stays open by design
        }

        var degradation = baseline <= 0 ? 0 : (underLoad - baseline) / baseline;
        Console.WriteLine($"BENCH-INTERACTIVE baseline-p95={baseline:F1}ms under-load-p95={underLoad:F1}ms degradation={degradation:P0}{BenchProvider.Tag(_store)}");
        // Budget (telco card): < 10% at the reference profile. Dev-machine noise gets a
        // small allowance; the recorded numbers in ops/jobs-benchmarks.md are the evidence.
        // #556: on Oracle the numbers are measured, not promised (oracle RFC §6), so the budget binds PostgreSQL only.
        Assert.True(_store.IsOracle || underLoad < baseline * 1.25 + 1.0,
            $"interactive p95 degraded {degradation:P0} (baseline {baseline:F1}ms → {underLoad:F1}ms)");
    }

    private sealed class WriteHeavyJob : IGoldpathJob
    {
        public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken ct)
            => Task.FromResult(GoldpathJobPlanner.ByRange(100_000, 250));

        public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken ct)
        {
            var db = context.Services.GetRequiredService<ClusterDb>();
            var (start, end) = GoldpathJobPlanner.ParseRange(chunk.Payload);
            for (var i = start; i < end; i += 50)
            {
                db.Sink.Add(new SinkEntry { JobName = "tilt", ChunkIndex = (int)i, Instance = "bench" });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private sealed class NoopJobChunkFactory
    {
        public GoldpathJobChunk Create(int index, string payload)
        {
            // Test-only construction path for the baseline loop (internal ctor).
            return (GoldpathJobChunk)Activator.CreateInstance(typeof(GoldpathJobChunk),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [index, payload], null)!;
        }
    }
}

/// <summary>
/// <see cref="JobsBenchTests"/> on Oracle (#556): a fresh schema on the Oracle collection's pinned container. Both traits
/// are on the class so the per-PR <c>oracle</c> lane (<c>Category=Oracle&amp;Category!=Bench</c>) never runs it.
/// </summary>
[Trait("Category", "Bench")]
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class JobsBenchOracleTests(OracleFixture oracle) : JobsBenchTests(SuiteStore.Oracle(oracle, "GP_JBENCH"));
