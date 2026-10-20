using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

public class AdminServiceTests : IDisposable
{
    private readonly BulkFixture _fixture = new();
    private readonly GoldpathBulkAdminService<BulkTestContext> _admin;

    public AdminServiceTests()
        => _admin = new GoldpathBulkAdminService<BulkTestContext>(
            _fixture.Engine, _fixture.Options,
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Upload_ingests_and_survives_a_missing_jobs_admin_surface()
    {
        // No GoldpathJobsAdminService registered in the fixture: the immediate trigger is
        // best-effort; the upload must still land (the cron is the safety net).
        var info = await _admin.UploadAsync("payments", BulkFixture.Csv(("E1", "TR1", "10", null)), "a.csv", null, "ops", CancellationToken.None);
        Assert.Equal("Received", info.State);
        Assert.Equal("payments", info.Definition);
        Assert.Equal(1, _fixture.Query(db => db.Set<GoldpathBulkBatch>().Count()));

        var again = await _admin.UploadAsync("payments", BulkFixture.Csv(("E1", "TR1", "10", null)), "a.csv", null, "ops", CancellationToken.None);
        Assert.Equal(info.Id, again.Id);   // dedup speaks through the verb too
    }

    [Fact]
    public async Task Definitions_view_reports_states_and_the_gate_age()
    {
        await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));
        await _admin.UploadAsync("payments", BulkFixture.Csv(("E9", "T", "9", null)), "b.csv", null, "ops", CancellationToken.None);

        var status = Assert.Single(await _admin.GetDefinitionsAsync(CancellationToken.None));
        Assert.Equal("payments", status.Name);
        Assert.Equal(1, status.BatchesByState["Validated"]);
        Assert.Equal(1, status.BatchesByState["Received"]);
        Assert.Equal(1, status.AwaitingApproval);
        Assert.NotNull(status.OldestAwaitingApprovalSeconds);
        Assert.True(status.OldestAwaitingApprovalSeconds >= 0);
    }

    [Fact]
    public async Task Batch_queries_filter_by_state_and_fail_closed_on_tenant()
    {
        var mine = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)), tenant: "agency-1");
        await _admin.UploadAsync("payments", BulkFixture.Csv(("E2", "T", "2", null)), "b.csv", "agency-2", "ops", CancellationToken.None);

        Assert.Equal(2, (await _admin.GetBatchesAsync(null, null, null, 10, CancellationToken.None)).Count);
        Assert.Single(await _admin.GetBatchesAsync(["validated"], null, null, 10, CancellationToken.None));
        // R3: several states OR together in one request.
        Assert.Equal(2, (await _admin.GetBatchesAsync(["validated", "received"], null, null, 10, CancellationToken.None)).Count);
        Assert.Equal(mine.Id, Assert.Single(await _admin.GetBatchesAsync(null, null, "agency-1", 10, CancellationToken.None)).Id);
        // H8 frozen contract: take rides AdminPaging.Clamp — the floor answers one row.
        Assert.Single(await _admin.GetBatchesAsync(null, null, null, 0, CancellationToken.None));

        Assert.NotNull(await _admin.GetBatchAsync(mine.Id, "agency-1", CancellationToken.None));
        Assert.Null(await _admin.GetBatchAsync(mine.Id, "agency-2", CancellationToken.None));   // fail-closed
        Assert.Null(await _admin.GetBatchAsync(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Take_is_clamped_at_the_ceiling_a_giant_ask_answers_exactly_500()
    {
        // H8 frozen contract, the D2 fix itself: take=1_000_000 must never become an
        // unbounded query — AdminPaging.MaxTake caps the page at 500.
        using (var scope = _fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
            for (var i = 0; i < 501; i++)
            {
                db.Set<GoldpathBulkBatch>().Add(new GoldpathBulkBatch
                {
                    Id = Guid.NewGuid(),
                    Definition = "payments",
                    FileId = Guid.NewGuid(),
                    State = GoldpathBulkBatchState.Received,
                    ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-i),
                });
            }

            await db.SaveChangesAsync();
        }

        Assert.Equal(500, (await _admin.GetBatchesAsync(null, null, null, 1_000_000, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Batches_filter_by_definition_on_the_server_and_compose_with_state()
    {
        // T8 (#72): the definition filter narrows the QUERY, not the returned page — a
        // definition whose newest batch sits far past any page must still answer.
        using (var scope = _fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
            db.Set<GoldpathBulkBatch>().Add(new GoldpathBulkBatch
            {
                Id = Guid.NewGuid(),
                Definition = "refunds",
                FileId = Guid.NewGuid(),
                State = GoldpathBulkBatchState.Validated,
                ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1),   // older than every payments row
            });
            for (var i = 0; i < 3; i++)
            {
                db.Set<GoldpathBulkBatch>().Add(new GoldpathBulkBatch
                {
                    Id = Guid.NewGuid(),
                    Definition = "payments",
                    FileId = Guid.NewGuid(),
                    State = GoldpathBulkBatchState.Received,
                    ReceivedAt = DateTimeOffset.UtcNow.AddSeconds(-i),
                });
            }

            await db.SaveChangesAsync();
        }

        var refunds = Assert.Single(await _admin.GetBatchesAsync(null, ["refunds"], null, 1, CancellationToken.None));
        Assert.Equal("refunds", refunds.Definition);   // take=1 would have shown only payments without the server filter

        // R3: several definitions OR together; definition ANDs with state.
        Assert.Equal(4, (await _admin.GetBatchesAsync(null, ["refunds", "payments"], null, 10, CancellationToken.None)).Count);
        Assert.Single(await _admin.GetBatchesAsync(["validated"], ["refunds", "payments"], null, 10, CancellationToken.None));
        Assert.Empty(await _admin.GetBatchesAsync(["received"], ["refunds"], null, 10, CancellationToken.None));
    }

    private void SeedBatches(params (string Definition, GoldpathBulkBatchState State, TimeSpan Age)[] batches)
    {
        var now = DateTimeOffset.UtcNow;
        _fixture.Mutate(db =>
        {
            foreach (var (definition, state, age) in batches)
            {
                db.Set<GoldpathBulkBatch>().Add(new GoldpathBulkBatch
                {
                    Id = Guid.NewGuid(),
                    Definition = definition,
                    FileId = Guid.NewGuid(),
                    State = state,
                    ReceivedAt = now - age,
                    ValidatedAt = state == GoldpathBulkBatchState.Validated ? now - age : null,
                });
            }
        });
    }

    /// <summary>The list contract ("recent batches, newest first"): the page is ordered by arrival, latest on top.</summary>
    [Fact]
    public async Task Batches_list_newest_first()
    {
        SeedBatches(
            ("payments", GoldpathBulkBatchState.Received, TimeSpan.FromHours(3)),
            ("payments", GoldpathBulkBatchState.Received, TimeSpan.FromHours(1)),
            ("payments", GoldpathBulkBatchState.Received, TimeSpan.FromHours(2)));

        var batches = await _admin.GetBatchesAsync(null, null, null, 10, CancellationToken.None);

        Assert.Equal(batches.OrderByDescending(b => b.ReceivedAt).Select(b => b.Id), batches.Select(b => b.Id));
        Assert.True(batches[0].ReceivedAt > batches[^1].ReceivedAt);
    }

    /// <summary>Contract R3: a repeatable filter given ZERO values is no filter — never "match nothing".</summary>
    [Fact]
    public async Task An_empty_definition_filter_is_no_filter()
    {
        SeedBatches(
            ("payments", GoldpathBulkBatchState.Received, TimeSpan.FromMinutes(1)),
            ("refunds", GoldpathBulkBatchState.Received, TimeSpan.FromMinutes(2)));

        Assert.Equal(2, (await _admin.GetBatchesAsync(null, [], null, 10, CancellationToken.None)).Count);
    }

    /// <summary>The gate-age gauge reports the OLDEST batch waiting at the gate, not the newest.</summary>
    [Fact]
    public async Task The_gate_age_is_the_oldest_waiting_batch()
    {
        SeedBatches(
            ("payments", GoldpathBulkBatchState.Validated, TimeSpan.FromMinutes(1)),
            ("payments", GoldpathBulkBatchState.Validated, TimeSpan.FromMinutes(30)));

        var status = Assert.Single(await _admin.GetDefinitionsAsync(CancellationToken.None));

        Assert.Equal(2, status.AwaitingApproval);
        Assert.InRange(status.OldestAwaitingApprovalSeconds!.Value, 30 * 60, 31 * 60);
    }

    /// <summary>
    /// Admin-contract R1 on the report: a batch's findings answer its own tenant and the
    /// all-tenants view, and a foreign tenant gets the same empty answer as an absent batch.
    /// </summary>
    [Fact]
    public async Task The_error_report_is_tenant_scoped_and_fails_closed()
    {
        var mine = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "-1", null)), tenant: "agency-1");
        await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E2", "T", "-2", null)), tenant: "agency-2");

        Assert.Single(await _admin.GetErrorsAsync(mine.Id, "agency-1", 0, 10, CancellationToken.None));
        Assert.Single(await _admin.GetErrorsAsync(mine.Id, null, 0, 10, CancellationToken.None));
        Assert.Empty(await _admin.GetErrorsAsync(mine.Id, "agency-2", 0, 10, CancellationToken.None));
    }

    /// <summary>D6 report order: several findings on ONE row page in the order validation recorded them.</summary>
    [Fact]
    public async Task Findings_on_one_row_keep_their_recorded_order()
    {
        using var fixture = new BulkFixture(b => b.Validate((_, ctx) =>
        {
            ctx.Fail("Iban", "iban checksum fails");
            ctx.Fail("Amount", "amount exceeds the daily limit");
        }));
        var admin = new GoldpathBulkAdminService<BulkTestContext>(
            fixture.Engine, fixture.Options, fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var batch = await fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));

        var report = await admin.GetErrorsAsync(batch.Id, null, 0, 10, CancellationToken.None);

        Assert.Equal(["Iban", "Amount"], report.Select(e => e.Field));
    }

    [Fact]
    public async Task The_error_report_pages_by_row_number()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(
            ("E1", "T", "-1", null), ("E2", "T", "-2", null), ("E3", "T", "-3", null)));

        var firstPage = await _admin.GetErrorsAsync(batch.Id, tenant: null, afterRowNumber: 0, take: 2, CancellationToken.None);
        Assert.Equal([1, 2], firstPage.Select(e => e.RowNumber));
        var secondPage = await _admin.GetErrorsAsync(batch.Id, tenant: null, afterRowNumber: firstPage[^1].RowNumber, take: 2, CancellationToken.None);
        Assert.Equal([3], secondPage.Select(e => e.RowNumber));
        Assert.All(firstPage, e => Assert.Equal("amount must be positive", e.Message));
    }

    [Fact]
    public async Task Gate_verbs_delegate_to_the_engine_with_the_actor()
    {
        var batch = await _fixture.IngestValidatedAsync(BulkFixture.Csv(("E1", "T", "1", null)));

        Assert.False((await _admin.RejectAsync(batch.Id, "ops", " ", CancellationToken.None)).Ok);   // reason mandatory
        Assert.True((await _admin.ApproveAsync(batch.Id, "treasurer", "four-eyes done", CancellationToken.None)).Ok);

        var decided = _fixture.Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
        Assert.Equal(GoldpathBulkBatchState.Approved, decided.State);
        Assert.Equal("treasurer", decided.DecidedBy);
    }
}
