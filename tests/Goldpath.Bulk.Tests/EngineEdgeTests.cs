using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>The engine's refusals, no-ops and terminal edges — every guard earns a test.</summary>
public class EngineEdgeTests : IDisposable
{
    private readonly BulkFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Verbs_on_a_missing_batch_teach_no_such_batch()
    {
        using var scope = _fixture.Scope();
        var approve = await _fixture.Engine.ApproveAsync(scope.ServiceProvider, Guid.NewGuid(), "x", null, CancellationToken.None);
        Assert.False(approve.Ok);   // a refusal, not a success with a sad message (the endpoint maps Ok to the status code)
        Assert.Equal("no such batch", approve.Message);
        var reject = await _fixture.Engine.RejectAsync(scope.ServiceProvider, Guid.NewGuid(), "x", "reason", CancellationToken.None);
        Assert.False(reject.Ok);
        Assert.Equal("no such batch", reject.Message);
    }

    [Fact]
    public async Task Approve_and_reject_refuse_non_validated_states_by_name()
    {
        using var scope = _fixture.Scope();
        var (batch, _) = await _fixture.Engine.IngestAsync(scope.ServiceProvider, "payments",
            BulkFixture.Csv(("E1", "T", "1", null)), "a.csv", null, CancellationToken.None);

        var approve = await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "x", null, CancellationToken.None);
        Assert.False(approve.Ok);
        Assert.Equal("only a Validated batch can be approved — this one is Received", approve.Message);

        var reject = await _fixture.Engine.RejectAsync(scope.ServiceProvider, batch.Id, "x", "reason", CancellationToken.None);
        Assert.False(reject.Ok);
        Assert.Equal("only a Validated batch can be rejected — this one is Received", reject.Message);
    }

    [Fact]
    public async Task A_clean_approval_says_exactly_approved_and_stamps_the_actor()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = _fixture.Scope();
        var result = await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", "note-7", CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal("approved", result.Message);

        var stamped = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal("treasurer", stamped.DecidedBy);
        Assert.Equal("note-7", stamped.DecisionNote);
        Assert.NotNull(stamped.DecidedAt);
    }

    [Fact]
    public async Task Validating_a_decided_batch_is_a_quiet_noop()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = _fixture.Scope();
        var rejected = await _fixture.Engine.RejectAsync(scope.ServiceProvider, batch.Id, "ops", "wrong file", CancellationToken.None);
        Assert.True(rejected.Ok);
        Assert.Equal("rejected", rejected.Message);

        await _fixture.Engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);

        var untouched = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal(GoldpathBulkBatchState.Rejected, untouched.State);   // the stale plan changed nothing
        Assert.Equal(1, _fixture.Query(db => db.Set<GoldpathBulkRow>().Count()));   // report intact
    }

    [Fact]
    public async Task Executing_a_range_of_a_non_executing_batch_is_a_quiet_noop()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = _fixture.Scope();
        var chunk = BulkFixture.MakeChunk(0, "x|1:2");
        await _fixture.Engine.ExecuteRangeAsync(scope.ServiceProvider, chunk, batch.Id, 1, 2, CancellationToken.None);

        Assert.Empty(_fixture.Handler.Executed);
        Assert.Equal(GoldpathBulkBatchState.Validated, _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id)).State);
    }

    [Fact]
    public async Task Replaying_an_already_executed_row_is_idempotent_evidence()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using (var scope = _fixture.Scope())
        {
            Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "ops", null, CancellationToken.None)).Ok);
        }

        await _fixture.ExecuteAllAsync(Guid.NewGuid());
        Assert.Single(_fixture.Handler.Executed);

        using (var scope = _fixture.Scope())
        {
            await _fixture.Engine.ReplayRowAsync(scope.ServiceProvider,
                GoldpathBulkEngine<BulkTestContext>.ItemKey(batch.Id, 1), CancellationToken.None);
        }

        Assert.Single(_fixture.Handler.Executed);   // NOT re-sent
        var counts = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal(1, counts.ExecutedRows);       // counters untouched
    }

    [Fact]
    public async Task Replaying_a_missing_row_fails_loudly()
    {
        using var scope = _fixture.Scope();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fixture.Engine.ReplayRowAsync(scope.ServiceProvider,
                GoldpathBulkEngine<BulkTestContext>.ItemKey(Guid.NewGuid(), 5), CancellationToken.None));
        Assert.Contains("No bulk row for repair item", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_batches_purge_their_files_by_CompletedAt()
    {
        using var fixture = new BulkFixture(b => b.DeleteFileAfter(TimeSpan.FromHours(1)));
        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = fixture.Scope();
        Assert.True((await fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "ops", null, CancellationToken.None)).Ok);
        await fixture.ExecuteAllAsync(Guid.NewGuid());

        fixture.Mutate(db => db.Set<GoldpathBulkBatch>().Single(b => b.Id == batch.Id).CompletedAt = DateTimeOffset.UtcNow.AddHours(-2));
        Assert.Equal(1, await fixture.Engine.PurgeExpiredFilesAsync(scope.ServiceProvider, CancellationToken.None));
        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathBulkFile>().Count()));
        Assert.Equal(0, await fixture.Engine.PurgeExpiredFilesAsync(scope.ServiceProvider, CancellationToken.None));   // idempotent
    }

    [Fact]
    public async Task Definitions_without_retention_never_purge()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        using var scope = _fixture.Scope();
        Assert.True((await _fixture.Engine.RejectAsync(scope.ServiceProvider, batch.Id, "ops", "keep the file", CancellationToken.None)).Ok);
        Assert.Equal(0, await _fixture.Engine.PurgeExpiredFilesAsync(scope.ServiceProvider, CancellationToken.None));
        Assert.Equal(1, _fixture.Query(db => db.Set<GoldpathBulkFile>().Count()));
    }

    [Fact]
    public async Task Adoption_orders_batches_by_arrival()
    {
        var late = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("L1", "T", "1", null)));
        var early = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        _fixture.Mutate(db =>
        {
            db.Set<GoldpathBulkBatch>().Single(b => b.Id == late.Id).State = GoldpathBulkBatchState.Approved;
            db.Set<GoldpathBulkBatch>().Single(b => b.Id == late.Id).ReceivedAt = DateTimeOffset.UtcNow;
            db.Set<GoldpathBulkBatch>().Single(b => b.Id == early.Id).State = GoldpathBulkBatchState.Approved;
            db.Set<GoldpathBulkBatch>().Single(b => b.Id == early.Id).ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        });

        using var scope = _fixture.Scope();
        var adopted = await _fixture.Engine.AdoptForExecutionAsync(scope.ServiceProvider, Guid.NewGuid(), CancellationToken.None);
        Assert.Equal([early.Id, late.Id], adopted.Select(b => b.Id));   // FIFO: first file in, first paid
    }
}
