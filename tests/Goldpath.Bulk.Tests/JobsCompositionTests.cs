using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// The jobs themselves: plan-payload GOLDEN shapes (payloads are wire contracts — a resumed
/// run re-parses them) and the end-to-end pass through the job classes rather than the
/// engine directly.
/// </summary>
public class JobsCompositionTests : IDisposable
{
    private readonly BulkFixture _fixture = new(chunkSize: 2);

    public void Dispose() => _fixture.Dispose();

    private static GoldpathJobContext CreateContext(IServiceProvider services, Guid? runId = null)
        => (GoldpathJobContext)Activator.CreateInstance(typeof(GoldpathJobContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [runId ?? Guid.NewGuid(), "test", "node", "job", false, null, services], null)!;

    private GoldpathBulkValidateJob<BulkTestContext> ValidateJob()
        => new(_fixture.Engine, _fixture.Options, TimeProvider.System);

    private GoldpathBulkExecuteJob<BulkTestContext> ExecuteJob()
        => new(_fixture.Engine, _fixture.Options);

    [Fact]
    public async Task Validate_plan_is_one_chunk_per_pending_batch_plus_the_purge_tail()
    {
        using var scope = _fixture.Scope();
        var (a, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E1", "T", "1", null)), "a.csv", null, CancellationToken.None);
        var (b, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E2", "T", "2", null)), "b.csv", null, CancellationToken.None);

        var plan = await ValidateJob().PlanAsync(CreateContext(scope.ServiceProvider), CancellationToken.None);

        Assert.Equal([$"validate:{a.Id:N}", $"validate:{b.Id:N}", "purge:files"], plan.ChunkPayloads);
        Assert.Equal(2, plan.TotalItems);
    }

    /// <summary>The intake run plans only what still needs validating: Received and Validating (a resumed chunk), never a decided batch.</summary>
    [Fact]
    public async Task Validate_plan_takes_only_received_and_resumed_batches()
    {
        using var scope = _fixture.Scope();
        var (received, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E1", "T", "1", null)), "a.csv", null, CancellationToken.None);
        var (resumed, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E2", "T", "2", null)), "b.csv", null, CancellationToken.None);
        await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E3", "T", "3", null)));
        _fixture.Mutate(db => db.Set<GoldpathBulkBatch>().Single(b => b.Id == resumed.Id).State = GoldpathBulkBatchState.Validating);

        var plan = await ValidateJob().PlanAsync(CreateContext(scope.ServiceProvider), CancellationToken.None);

        Assert.Equal([$"validate:{received.Id:N}", $"validate:{resumed.Id:N}", "purge:files"], plan.ChunkPayloads);
    }

    /// <summary>D6: the intake run's trailing <c>purge:files</c> chunk IS the retention sweep.</summary>
    [Fact]
    public async Task The_purge_tail_chunk_deletes_expired_files()
    {
        using var fixture = new BulkFixture(b => b.DeleteFileAfter(TimeSpan.FromHours(1)));
        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = fixture.Scope();
        Assert.True((await fixture.Engine.RejectAsync(scope.ServiceProvider, batch.Id, "ops", "wrong cutoff", CancellationToken.None)).Ok);
        fixture.Mutate(db => db.Set<GoldpathBulkBatch>().Single(b => b.Id == batch.Id).DecidedAt = DateTimeOffset.UtcNow.AddHours(-2));

        await new GoldpathBulkValidateJob<BulkTestContext>(fixture.Engine, fixture.Options, TimeProvider.System)
            .ExecuteChunkAsync(BulkFixture.MakeChunk(0, "purge:files"), CreateContext(scope.ServiceProvider), CancellationToken.None);

        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathBulkFile>().Count()));
    }

    [Fact]
    public async Task Execute_plan_chunks_each_adopted_batch_over_its_row_number_space()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(
            ("E1", "T", "1", null), ("E2", "T", "2", null), ("E3", "T", "3", null),
            ("E4", "T", "4", null), ("E5", "T", "5", null)));
        using var scope = _fixture.Scope();
        Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "ops", null, CancellationToken.None)).Ok);

        var runId = Guid.NewGuid();
        var plan = await ExecuteJob().PlanAsync(CreateContext(scope.ServiceProvider, runId), CancellationToken.None);

        // ChunkSize 2 over row numbers 1..5: golden ranges, end-exclusive.
        Assert.Equal([$"{batch.Id:N}|1:3", $"{batch.Id:N}|3:5", $"{batch.Id:N}|5:6"], plan.ChunkPayloads);
        Assert.Equal(5, plan.TotalItems);
        Assert.Equal(runId, _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id)).RunId);
    }

    [Fact]
    public async Task The_whole_story_runs_through_the_job_classes()
    {
        using var scope = _fixture.Scope();
        var (batch, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E1", "TR1", "10", null), ("E2", "TR2", "20", null), ("E3", "TR3", "30", null)),
            "payments.csv", null, CancellationToken.None);

        // VALIDATE via the job.
        var validateContext = CreateContext(scope.ServiceProvider);
        var validatePlan = await ValidateJob().PlanAsync(validateContext, CancellationToken.None);
        for (var i = 0; i < validatePlan.ChunkPayloads.Count; i++)
        {
            await ValidateJob().ExecuteChunkAsync(BulkFixture.MakeChunk(i, validatePlan.ChunkPayloads[i]), validateContext, CancellationToken.None);
        }

        Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "ops", null, CancellationToken.None)).Ok);

        // EXECUTE via the job.
        var executeContext = CreateContext(scope.ServiceProvider);
        var executePlan = await ExecuteJob().PlanAsync(executeContext, CancellationToken.None);
        for (var i = 0; i < executePlan.ChunkPayloads.Count; i++)
        {
            await ExecuteJob().ExecuteChunkAsync(BulkFixture.MakeChunk(i, executePlan.ChunkPayloads[i]), executeContext, CancellationToken.None);
        }

        var done = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal(GoldpathBulkBatchState.Completed, done.State);
        Assert.Equal(3, done.ExecutedRows);
        Assert.Equal(3, _fixture.Handler.Executed.Count);
    }

    [Fact]
    public void Item_keys_round_trip()
    {
        var batchId = Guid.NewGuid();
        var key = GoldpathBulkEngine<BulkTestContext>.ItemKey(batchId, 42);
        Assert.Equal($"{batchId:N}#42", key);
        Assert.Equal((batchId, 42), GoldpathBulkEngine<BulkTestContext>.ParseItemKey(key));
    }

    [Fact]
    public void The_jobs_registration_wires_both_runs_with_deadlines()
    {
        var jobs = new GoldpathJobsOptions();
        jobs.AddGoldpathBulkJobs<BulkTestContext>();

        Assert.Equal(2, jobs.Jobs.Count);
        var validate = jobs.Jobs.Single(j => j.Name == "GoldpathBulkValidateJob`1");
        var execute = jobs.Jobs.Single(j => j.Name == "GoldpathBulkExecuteJob`1");
        Assert.Equal("0 * * * * ?", validate.Cron);
        Assert.Equal("30 * * * * ?", execute.Cron);
        Assert.NotNull(validate.Deadline);   // GP1302: an SLA is part of the definition
        Assert.NotNull(execute.Deadline);
        Assert.Equal(1, execute.MaxParallelChunks);   // payment-shaped order by default
    }

}
