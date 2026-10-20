using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// One execute chunk observed MID-batch, where the terminal recount cannot paper over a
/// wrong increment (bulk RFC D4; MDM constraints 2 and 4): a chunk runs exactly its row
/// range, claims its rows before any handler runs, stops at cancellation, and moves the
/// batch counters by exactly what it did — and so does a replay.
/// </summary>
public class ExecutionLedgerTests : IDisposable
{
    private readonly BulkFixture _fixture = new(chunkSize: 2);

    public void Dispose() => _fixture.Dispose();

    private async Task<GoldpathBulkBatch> ExecutingAsync(params (string Id, string Iban, string Amount, string? Note)[] rows)
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(rows));
        using var scope = _fixture.Scope();
        Assert.True((await _fixture.Engine.ApproveAsync(scope.ServiceProvider, batch.Id, "treasurer", null, CancellationToken.None)).Ok);
        Assert.Single(await _fixture.Engine.AdoptForExecutionAsync(scope.ServiceProvider, Guid.NewGuid(), CancellationToken.None));
        return batch;
    }

    private async Task RunRangeAsync(Guid batchId, long start, long end, CancellationToken cancellationToken = default)
    {
        using var scope = _fixture.Scope();
        var chunk = BulkFixture.MakeChunk(0, $"{batchId:N}|{start}:{end}");
        await _fixture.Engine.ExecuteRangeAsync(scope.ServiceProvider, chunk, batchId, start, end, cancellationToken);
    }

    private GoldpathBulkBatch Batch(Guid id)
        => _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == id));

    private List<GoldpathBulkRow> Rows(Guid id)
        => _fixture.Query(db => db.Set<GoldpathBulkRow>().AsNoTracking().Where(r => r.BatchId == id).OrderBy(r => r.RowNumber).ToList());

    /// <summary>
    /// The range is end-exclusive over row numbers (the plan's golden payload <c>1:3</c>), the
    /// handler is told this is NOT a replay, and the counters move by the chunk's own outcome:
    /// one executed, one failed into the repair queue, while rows 3–4 stay untouched.
    /// </summary>
    [Fact]
    public async Task A_chunk_runs_exactly_its_range_and_moves_the_counters_by_what_it_did()
    {
        var batch = await ExecutingAsync(
            ("E1", "TR1", "10", null), ("E2", "TR2", "20", "FAIL"), ("E3", "TR3", "30", null), ("E4", "TR4", "40", null));
        var reached = new List<int>();
        _fixture.Handler.Probe = (_, context) => reached.Add(context.RowNumber);

        await RunRangeAsync(batch.Id, 1, 3);

        Assert.Equal([1, 2], reached);
        var (_, context) = Assert.Single(_fixture.Handler.Executed);
        Assert.False(context.Replay, "a first execution is not a replay — handlers branch on this flag");

        var mid = Batch(batch.Id);
        Assert.Equal(GoldpathBulkBatchState.Executing, mid.State);   // rows 3-4 still open: no terminal recount yet
        Assert.Equal(1, mid.ExecutedRows);
        Assert.Equal(1, mid.FailedRows);
        Assert.All(Rows(batch.Id).Where(r => r.RowNumber > 2), r => Assert.Null(r.ClaimedAt));
    }

    /// <summary>MDM constraint 2: a row found claimed-but-unstamped counts as FAILED the moment the chunk sees it.</summary>
    [Fact]
    public async Task An_interrupted_row_counts_as_failed_mid_batch()
    {
        var batch = await ExecutingAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", null), ("E3", "TR3", "30", null));
        _fixture.Mutate(db => db.Set<GoldpathBulkRow>().Single(r => r.BatchId == batch.Id && r.RowNumber == 1).ClaimedAt = DateTimeOffset.UtcNow);

        await RunRangeAsync(batch.Id, 1, 3);

        var mid = Batch(batch.Id);
        Assert.Equal(GoldpathBulkBatchState.Executing, mid.State);
        Assert.Equal(1, mid.ExecutedRows);
        Assert.Equal(1, mid.FailedRows);
    }

    /// <summary>
    /// MDM constraint 2, the claim itself: when the handler runs, EVERY row of its chunk is
    /// already claimed in the database — a crash from here on repairs instead of re-sending.
    /// </summary>
    [Fact]
    public async Task Rows_are_claimed_in_the_database_before_any_handler_runs()
    {
        var batch = await ExecutingAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", null));
        var claimedWhenCalled = new List<bool>();
        _fixture.Handler.Probe = (_, context) => claimedWhenCalled.Add(_fixture.Query(db => db.Set<GoldpathBulkRow>().AsNoTracking()
            .Where(r => r.BatchId == context.BatchId && r.RowNumber <= 2)
            .All(r => r.ClaimedAt != null)));

        await RunRangeAsync(batch.Id, 1, 3);

        Assert.Equal([true, true], claimedWhenCalled);
    }

    /// <summary>Shutdown mid-chunk: no further row reaches the handler; the claims stay for the resumed attempt.</summary>
    [Fact]
    public async Task Cancellation_stops_the_chunk_before_the_next_row()
    {
        var batch = await ExecutingAsync(("E1", "TR1", "10", null), ("E2", "TR2", "20", null));
        using var shutdown = new CancellationTokenSource();
        _fixture.Handler.Probe = (_, _) => shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunRangeAsync(batch.Id, 1, 3, shutdown.Token));

        Assert.Equal([1], _fixture.Handler.Executed.Select(e => e.Context.RowNumber));
        Assert.All(Rows(batch.Id), r => Assert.NotNull(r.ClaimedAt));
    }

    /// <summary>
    /// A replay fixes the counters by exactly one in each direction — observed while other
    /// chunks are still open, so the terminal recount is not the one doing the arithmetic.
    /// </summary>
    [Fact]
    public async Task A_replay_moves_one_row_from_failed_to_executed_mid_batch()
    {
        var batch = await ExecutingAsync(
            ("E1", "TR1", "10", "FAIL"), ("E2", "TR2", "20", null), ("E3", "TR3", "30", null), ("E4", "TR4", "40", null));
        await RunRangeAsync(batch.Id, 1, 3);
        Assert.Equal((1, 1), (Batch(batch.Id).ExecutedRows, Batch(batch.Id).FailedRows));

        _fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathBulkRow>().Single(r => r.BatchId == batch.Id && r.RowNumber == 1);
            row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);   // the downstream got fixed
        });
        using (var scope = _fixture.Scope())
        {
            await _fixture.Engine.ReplayRowAsync(scope.ServiceProvider,
                GoldpathBulkEngine<BulkTestContext>.ItemKey(batch.Id, 1), CancellationToken.None);
        }

        var mid = Batch(batch.Id);
        Assert.Equal(GoldpathBulkBatchState.Executing, mid.State);
        Assert.Equal(2, mid.ExecutedRows);
        Assert.Equal(0, mid.FailedRows);
    }
}
