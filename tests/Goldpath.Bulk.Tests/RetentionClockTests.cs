using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// File retention on a frozen clock (bulk RFC D6, <c>DeleteFileAfter</c>): the period runs
/// from the batch's terminal stamp and elapses strictly AFTER it, only terminal batches
/// release their bytes, and a replay that flips the batch to Completed does not restart it.
/// </summary>
public class RetentionClockTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig : IDisposable
    {
        public Rig(Action<GoldpathBulkBatchBuilder<PaymentRow>> configure)
        {
            Fixture = new BulkFixture(configure);
            Engine = new GoldpathBulkEngine<BulkTestContext>(
                Fixture.Options, Fixture.Store, Clock, NullLogger<GoldpathBulkEngine<BulkTestContext>>.Instance);
        }

        public FrozenClock Clock { get; } = new(T0);

        public BulkFixture Fixture { get; }

        public GoldpathBulkEngine<BulkTestContext> Engine { get; }

        public async Task<GoldpathBulkBatch> ValidatedAsync(MemoryStream csv)
        {
            using var scope = Fixture.Scope();
            var (batch, _) = await Engine.IngestAsync(scope.ServiceProvider, "payments", csv, "batch.csv", null, CancellationToken.None);
            await Engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);
            return Batch(batch.Id);
        }

        public async Task ApproveAsync(Guid batchId)
        {
            using var scope = Fixture.Scope();
            Assert.True((await Engine.ApproveAsync(scope.ServiceProvider, batchId, "treasurer", null, CancellationToken.None)).Ok);
        }

        public async Task ExecuteAllAsync()
        {
            using var scope = Fixture.Scope();
            foreach (var batch in await Engine.AdoptForExecutionAsync(scope.ServiceProvider, Guid.NewGuid(), CancellationToken.None))
            {
                var chunk = BulkFixture.MakeChunk(0, $"{batch.Id:N}|1:{batch.TotalRows + 1}");
                await Engine.ExecuteRangeAsync(scope.ServiceProvider, chunk, batch.Id, 1, batch.TotalRows + 1, CancellationToken.None);
            }
        }

        public async Task<int> PurgeAsync()
        {
            using var scope = Fixture.Scope();
            return await Engine.PurgeExpiredFilesAsync(scope.ServiceProvider, CancellationToken.None);
        }

        public GoldpathBulkBatch Batch(Guid id)
            => Fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == id));

        public bool FileExists(Guid fileId)
            => Fixture.Query(db => db.Set<GoldpathBulkFile>().Any(f => f.Id == fileId));

        public void Dispose() => Fixture.Dispose();
    }

    /// <summary>
    /// An auto-approved batch decided AND completed at T0 keeps its file through T0 + period,
    /// and releases it a millisecond later — for BOTH stamps the purge reads.
    /// </summary>
    [Fact]
    public async Task Retention_elapses_strictly_after_the_period_from_the_terminal_stamp()
    {
        using var rig = new Rig(b => b.AutoApprove().DeleteFileAfter(TimeSpan.FromHours(1)));
        var batch = await rig.ValidatedAsync(BulkFixture.Csv(("E1", "TR1", "10", null)));
        await rig.ExecuteAllAsync();
        var done = rig.Batch(batch.Id);
        Assert.Equal(GoldpathBulkBatchState.Completed, done.State);
        Assert.Equal((T0, T0), (done.DecidedAt, done.CompletedAt));

        rig.Clock.Now = T0.AddHours(1);
        Assert.Equal(0, await rig.PurgeAsync());
        Assert.True(rig.FileExists(batch.FileId), "at exactly the retention period the bytes are still owed");

        // One millisecond: the test store's DateTimeOffset converter keeps 0.1 ms, not ticks.
        rig.Clock.Now = T0.AddHours(1).AddMilliseconds(1);
        Assert.Equal(1, await rig.PurgeAsync());
        Assert.False(rig.FileExists(batch.FileId));
    }

    /// <summary>
    /// D6 retention is for TERMINAL batches: an approved batch still waiting to execute keeps
    /// its bytes however old its decision is, while a batch that finished with failures
    /// releases them like any other terminal state.
    /// </summary>
    [Fact]
    public async Task Only_terminal_batches_release_their_files()
    {
        using var rig = new Rig(b => b.DeleteFileAfter(TimeSpan.FromHours(1)));
        var withFailures = await rig.ValidatedAsync(BulkFixture.Csv(("E1", "TR1", "10", "FAIL")));
        await rig.ApproveAsync(withFailures.Id);
        await rig.ExecuteAllAsync();
        Assert.Equal(GoldpathBulkBatchState.CompletedWithFailures, rig.Batch(withFailures.Id).State);

        var waiting = await rig.ValidatedAsync(BulkFixture.Csv(("E2", "TR2", "20", null)));
        await rig.ApproveAsync(waiting.Id);   // decided at T0, never executed
        Assert.Equal(GoldpathBulkBatchState.Approved, rig.Batch(waiting.Id).State);

        rig.Clock.Now = T0.AddHours(2);
        Assert.Equal(1, await rig.PurgeAsync());
        Assert.False(rig.FileExists(withFailures.FileId));
        Assert.True(rig.FileExists(waiting.FileId), "an approved batch has not executed yet — its file is still the evidence");
    }

    /// <summary>The replay flip to Completed keeps the FIRST completion time — retention does not restart.</summary>
    [Fact]
    public async Task A_replay_flip_keeps_the_first_completion_time()
    {
        using var rig = new Rig(b => b.DeleteFileAfter(TimeSpan.FromHours(1)));
        var batch = await rig.ValidatedAsync(BulkFixture.Csv(("E1", "TR1", "10", "FAIL")));
        await rig.ApproveAsync(batch.Id);
        await rig.ExecuteAllAsync();
        Assert.Equal(T0, rig.Batch(batch.Id).CompletedAt);

        rig.Fixture.Mutate(db =>
        {
            var row = db.Set<GoldpathBulkRow>().Single(r => r.BatchId == batch.Id);
            row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
        });
        rig.Clock.Now = T0.AddMinutes(30);
        using (var scope = rig.Fixture.Scope())
        {
            await rig.Engine.ReplayRowAsync(scope.ServiceProvider,
                GoldpathBulkEngine<BulkTestContext>.ItemKey(batch.Id, 1), CancellationToken.None);
        }

        var healed = rig.Batch(batch.Id);
        Assert.Equal(GoldpathBulkBatchState.Completed, healed.State);
        Assert.Equal(T0, healed.CompletedAt);
    }
}
