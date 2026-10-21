using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// The validate chunk from the inside: the state machine's <c>Validating</c> step is durable
/// while rows are checked (D2), report rows land in batches of <c>InsertBatchSize</c> (MDM
/// constraint 4), and the row ceiling is exact and refuses the batch WHOLE — including rows
/// an earlier flush already wrote (D5/GP1501).
/// </summary>
public class ValidationFlowTests
{
    /// <summary>D2: while its rows are being validated a batch reads <c>Validating</c> — not <c>Received</c> — from the store.</summary>
    [Fact]
    public async Task A_batch_reads_Validating_in_the_store_while_its_rows_are_checked()
    {
        var seen = new List<GoldpathBulkBatchState>();
        using var fixture = new BulkFixture(b => b.Validate((_, ctx) => seen.Add(
            ctx.Services.GetRequiredService<BulkTestContext>().Set<GoldpathBulkBatch>().AsNoTracking().Single().State)));

        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "TR1", "10", null), ("E2", "TR2", "20", null)));

        Assert.Equal([GoldpathBulkBatchState.Validating, GoldpathBulkBatchState.Validating], seen);
        Assert.Equal(GoldpathBulkBatchState.Validated, batch.State);
    }

    /// <summary>
    /// MDM constraint 4 / <see cref="GoldpathBulkOptions.InsertBatchSize"/>: rows and findings
    /// are written in saves of at most that many — and every one of them lands exactly once.
    /// </summary>
    [Fact]
    public async Task Report_rows_are_written_in_batches_of_InsertBatchSize()
    {
        using var fixture = new BulkFixture();
        fixture.Options.InsertBatchSize = 2;
        var perSave = new List<int>();

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        var (batch, _) = await fixture.Engine.IngestAsync(scope.ServiceProvider, "payments", BulkFixture.Csv(
            ("E1", "TR1", "10", null), ("E2", "TR2", "-1", null), ("E3", "TR3", "30", null),
            ("E4", "TR4", "-4", null), ("E5", "TR5", "50", null)), "batch.csv", null, CancellationToken.None);
        db.SavingChanges += (sender, _) =>
        {
            var tracker = ((DbContext)sender!).ChangeTracker;
            var added = tracker.Entries<GoldpathBulkRow>().Count(e => e.State == EntityState.Added)
                + tracker.Entries<GoldpathBulkRowError>().Count(e => e.State == EntityState.Added);
            if (added > 0)
            {
                perSave.Add(added);
            }
        };

        await fixture.Engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);

        Assert.Equal([2, 2, 1], perSave);
        Assert.Equal([1, 3, 5], fixture.Query(d => d.Set<GoldpathBulkRow>().OrderBy(r => r.RowNumber).Select(r => r.RowNumber).ToList()));
        Assert.Equal([2, 4], fixture.Query(d => d.Set<GoldpathBulkRowError>().OrderBy(e => e.RowNumber).Select(e => e.RowNumber).ToList()));
    }

    /// <summary>GP1501: the ceiling is a MAXIMUM — a file of exactly <c>MaxRows</c> rows is accepted.</summary>
    [Fact]
    public async Task A_file_of_exactly_MaxRows_rows_is_within_the_ceiling()
    {
        using var fixture = new BulkFixture(maxRows: 2);

        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null), ("E2", "T", "2", null)));

        Assert.Equal(GoldpathBulkBatchState.Validated, batch.State);
        Assert.Equal(2, batch.TotalRows);
        Assert.Equal(2, batch.ValidRows);
    }

    /// <summary>
    /// D5: a ceiling hit refuses the batch WHOLE — rows an earlier flush already persisted are
    /// wiped, nothing executable survives, and the decision note says why.
    /// </summary>
    [Fact]
    public async Task A_ceiling_hit_wipes_rows_an_earlier_flush_wrote_and_records_why()
    {
        using var fixture = new BulkFixture(maxRows: 3);
        fixture.Options.InsertBatchSize = 2;

        var refused = await fixture.IngestValidatedAsync(BulkFixture.Csv(
            ("E1", "T", "1", null), ("E2", "T", "2", null), ("E3", "T", "3", null), ("E4", "T", "4", null), ("E5", "T", "5", null)));

        Assert.Equal(GoldpathBulkBatchState.Rejected, refused.State);
        Assert.Equal("row ceiling exceeded", refused.DecisionNote);
        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathBulkRow>().Count(r => r.BatchId == refused.Id)));
    }
}
