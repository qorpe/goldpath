using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The finance card's bulk story on real PostgreSQL with the REAL jobs runner: upload a
/// payment file (with seeded poison) → the validate RUN writes the report → the gate →
/// the execute RUN pays the valid rows, isolates the poisoned ones into the repair queue →
/// the admin `replay-items` verb heals them → counts reconcile EXACTLY (no row lost, no
/// row double-executed). Plus content-hash dedup and the gate's refusals, end to end. The same
/// facts run on Oracle with the clustered Quartz store on Oracle (<see cref="BulkOracleTests"/>,
/// G7 ORA-6 #409): the claims are conditional updates (RFC goldpath-oracle §2.6), so nothing
/// changes for that provider.
/// </summary>
[Collection("quartz-process-globals")]
public class BulkTests : IAsyncLifetime
{
    private readonly SuiteStore _store;

    /// <summary>The suite on PostgreSQL, in the integration lane.</summary>
    public BulkTests()
        : this(SuiteStore.PostgreSql())
    {
    }

    /// <summary>The suite on the given store (the Oracle twin, ORA-6 #409).</summary>
    protected BulkTests(SuiteStore store) => _store = store;

    public sealed class PaymentRow
    {
        public string EndToEndId { get; set; } = "";
        public string Iban { get; set; } = "";
        public decimal Amount { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>The side-effect ledger: one row per handler execution (double-send detector).</summary>
    public sealed class PaymentSink
    {
        public long Id { get; set; }
        public string EndToEndId { get; set; } = "";
        public int RowNumber { get; set; }

        /// <summary>The ambient trace at execution — proves the handler ran INSIDE the run's spans (H4).</summary>
        public string? TraceId { get; set; }
    }

    public sealed class BulkDb(DbContextOptions<BulkDb> options) : DbContext(options)
    {
        public DbSet<PaymentSink> Sink => Set<PaymentSink>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddGoldpathBulk();
            modelBuilder.AddGoldpathJobs();
        }
    }

    /// <summary>Pays one row: appends to the sink; "FAIL"-noted rows throw (poison).</summary>
    public sealed class PaymentHandler(BulkDb db) : IGoldpathBulkRowHandler<PaymentRow>
    {
        public async Task ExecuteAsync(PaymentRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken)
        {
            if (row.Note == "FAIL")
            {
                throw new InvalidOperationException("core banking refused the instruction");
            }

            db.Sink.Add(new PaymentSink
            {
                EndToEndId = row.EndToEndId,
                RowNumber = context.RowNumber,
                TraceId = Activity.Current?.TraceId.ToHexString(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private readonly string _fleet = $"bulk-{Guid.NewGuid():N}"[..16];   // unique per test: Quartz's SchedulerRepository is process-global
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _store.StartAsync<BulkDb>(options => new(options));

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:bulkdb"] = _store.ConnectionString;
        builder.Services.AddDbContext<BulkDb>(_store.Use);
        builder.Services.AddScoped<IGoldpathBulkRowHandler<PaymentRow>, PaymentHandler>();
        builder.AddGoldpathBulk<HostApplicationBuilder, BulkDb>(bulk =>
        {
            bulk.ChunkSize = 3;   // several checkpoints even in a small file
            bulk.AddBatch<PaymentRow>("payments", b => b
                .MaxRows(100_000)
                .RowKey(r => r.EndToEndId)
                .Validate((row, ctx) =>
                {
                    if (row.Amount <= 0)
                    {
                        ctx.Fail(nameof(row.Amount), "amount must be positive");
                    }
                }));
        });
        builder.AddGoldpathJobs<HostApplicationBuilder, BulkDb>(jobs =>
        {
            jobs.ConnectionName = "bulkdb";
            jobs.Provider = _store.JobsProvider;
            jobs.SchedulerName = _fleet;
            // Far-future crons: the test drives every run through the ADMIN verb, like an operator.
            jobs.AddGoldpathBulkJobs<BulkDb>(validateCron: "0 0 0 1 1 ? 2099", executeCron: "0 0 0 1 1 ? 2099");
        });
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        QuartzProcessGlobals.Pin();   // the disposed host may own Quartz's global log provider
        await _store.StopAsync();
    }

    private static MemoryStream Csv(int rows, params int[] poisoned)
    {
        var text = new StringBuilder("EndToEndId,Iban,Amount,Note\n");
        for (var i = 1; i <= rows; i++)
        {
            text.Append($"E{i},TR{i:D2},{i * 10},{(poisoned.Contains(i) ? "FAIL" : "")}\n");
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private GoldpathJobsAdminService<BulkDb> Admin => _host.Services.GetRequiredService<GoldpathJobsAdminService<BulkDb>>();

    private GoldpathBulkAdminService<BulkDb> BulkAdmin => _host.Services.GetRequiredService<GoldpathBulkAdminService<BulkDb>>();

    private GoldpathBulkEngine<BulkDb> Engine => _host.Services.GetRequiredService<GoldpathBulkEngine<BulkDb>>();

    private T Query<T>(Func<BulkDb, T> query)
    {
        using var scope = _host.Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<BulkDb>());
    }

    /// <summary>D9 discovery is store-driven: wait until the executor persisted its jobs before verbing.</summary>
    private async Task WaitForFleetAsync(CancellationToken token)
    {
        while (true)
        {
            var jobs = await Admin.GetJobsAsync(_fleet, token);
            if (jobs.Count >= 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    private async Task<GoldpathBulkBatch> WaitForStateAsync(Guid batchId, GoldpathBulkBatchState state, CancellationToken token)
    {
        while (true)
        {
            var batch = Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batchId));
            if (batch.State == state)
            {
                return batch;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    [Fact]
    public async Task The_finance_story_holds_end_to_end_with_repair_and_replay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        // UPLOAD through the ADMIN VERB: 10 rows, rows 4 and 8 poisoned at EXECUTION
        // (validation-clean). The verb fires the validate run IMMEDIATELY — no manual
        // trigger and no waiting for the cron safety net.
        await WaitForFleetAsync(timeout.Token);
        var uploaded = await BulkAdmin.UploadAsync("payments", Csv(10, 4, 8), "payments.csv", null, "it-operator", timeout.Token);
        var batchId = uploaded.Id;

        // DEDUP speaks through the verb too: the retry storm answers with the same batch.
        var again = await BulkAdmin.UploadAsync("payments", Csv(10, 4, 8), "payments.csv", null, "it-operator", timeout.Token);
        Assert.Equal(batchId, again.Id);

        var validated = await WaitForStateAsync(batchId, GoldpathBulkBatchState.Validated, timeout.Token);
        Assert.Equal(10, validated.TotalRows);
        Assert.Equal(10, validated.ValidRows);   // poison is an EXECUTION failure, not a validation one

        // The definitions view sees the batch waiting at the gate.
        var status = Assert.Single(await BulkAdmin.GetDefinitionsAsync(timeout.Token));
        Assert.Equal(1, status.AwaitingApproval);

        // THE GATE (actor stamped), then EXECUTE as a real run.
        Assert.True((await BulkAdmin.ApproveAsync(batchId, "treasurer", "four-eyes done", timeout.Token)).Ok);

        Assert.True((await Admin.TriggerAsync(_fleet, "GoldpathBulkExecuteJob`1", dryRun: false, "it-operator", timeout.Token)).Ok);
        var partial = await WaitForStateAsync(batchId, GoldpathBulkBatchState.CompletedWithFailures, timeout.Token);
        Assert.Equal(8, partial.ExecutedRows);
        Assert.Equal(2, partial.FailedRows);
        Assert.NotNull(partial.RunId);

        // The repair queue holds EXACTLY the poisoned rows; the sink holds each paid row ONCE.
        var detail = await Admin.GetRunAsync(partial.RunId!.Value, timeout.Token);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.OpenFailures.Count);
        Assert.All(detail.OpenFailures, f => Assert.Contains("core banking refused", f.Reason, StringComparison.Ordinal));
        Assert.Equal(8, Query(db => db.Sink.Count()));
        Assert.Equal(8, Query(db => db.Sink.Select(s => s.RowNumber).Distinct().Count()));

        // HEAL the world, then the admin replay verb — rows 4 and 8 pay, the batch flips.
        Query(db =>
        {
            foreach (var row in db.Set<GoldpathBulkRow>().Where(r => r.BatchId == batchId && r.FailedAt != null))
            {
                row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
            }

            return db.SaveChanges();
        });
        Assert.True((await Admin.ReplayItemsAsync(partial.RunId.Value, "it-operator", timeout.Token)).Ok);

        var healed = await WaitForStateAsync(batchId, GoldpathBulkBatchState.Completed, timeout.Token);
        Assert.Equal(10, healed.ExecutedRows);
        Assert.Equal(0, healed.FailedRows);
        Assert.Equal(10, Query(db => db.Sink.Count()));
        Assert.Equal(10, Query(db => db.Sink.Select(s => s.RowNumber).Distinct().Count()));   // exactly once, each
    }

    [Fact]
    public async Task The_gate_refuses_a_dirty_batch_and_a_reasonless_rejection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Guid batchId;
        using (var scope = _host.Services.CreateScope())
        {
            var csv = new MemoryStream(Encoding.UTF8.GetBytes("EndToEndId,Iban,Amount,Note\nD1,TR1,-5,\nD2,TR2,10,\n"));
            var (batch, _) = await Engine.IngestAsync(scope.ServiceProvider, "payments", csv, "dirty.csv", null, timeout.Token);
            batchId = batch.Id;
            await Engine.ValidateBatchAsync(scope.ServiceProvider, batchId, timeout.Token);

            var approve = await Engine.ApproveAsync(scope.ServiceProvider, batchId, "treasurer", null, timeout.Token);
            Assert.False(approve.Ok);
            Assert.Contains("invalid rows block approval", approve.Message, StringComparison.Ordinal);

            Assert.False((await Engine.RejectAsync(scope.ServiceProvider, batchId, "treasurer", "", timeout.Token)).Ok);
            Assert.True((await Engine.RejectAsync(scope.ServiceProvider, batchId, "treasurer", "negative amount on row 1", timeout.Token)).Ok);
        }

        var rejected = Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batchId));
        Assert.Equal(GoldpathBulkBatchState.Rejected, rejected.State);
        Assert.Equal("treasurer", rejected.DecidedBy);
    }

    /// <summary>Polls the recorder for a span matching <paramref name="match"/> (spans stop async to state flips).</summary>
    private static async Task<Activity> WaitForSpanAsync(List<Activity> spans, Func<Activity, bool> match, CancellationToken token)
    {
        while (true)
        {
            lock (spans)
            {
                if (spans.FirstOrDefault(match) is { } span)
                {
                    return span;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
    }

    /// <summary>
    /// H4: ONE trace id follows an instruction across every process boundary. The upload
    /// request's trace is pinned on the batch and LINKED from the validate/execute spans;
    /// an operator's trigger trace is carried over the Quartz store and LINKED from the
    /// run span; the row handler executes INSIDE the run's trace (its downstream calls
    /// inherit it); and the replay verb's trace reaches the replayed row, which still
    /// links back to the original upload. No orphan spans, no broken chain.
    /// </summary>
    [Fact]
    public async Task One_trace_id_walks_from_upload_through_execution_to_replay()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var spans = new List<Activity>();
        using var entry = new ActivitySource("it-entry");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("Goldpath.", StringComparison.Ordinal) || s.Name == "it-entry",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (spans)
                {
                    spans.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await WaitForFleetAsync(timeout.Token);

        // The "HTTP entry" stand-in for ASP.NET's server span — the seam under proof is
        // OURS: capture at ingest, pin on the batch, link from every later span.
        ActivityTraceId uploadTrace;
        Guid batchId;
        using (var upload = entry.StartActivity("POST /goldpath/admin/bulk/upload"))
        {
            uploadTrace = upload!.TraceId;
            batchId = (await BulkAdmin.UploadAsync("payments", Csv(4, 2), "trace.csv", null, "it-operator", timeout.Token)).Id;
        }

        // 1) The batch pinned the upload request's traceparent.
        var stored = Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batchId));
        Assert.NotNull(stored.TraceParent);
        Assert.Contains(uploadTrace.ToHexString(), stored.TraceParent);

        // 2) The validate RUN — a Quartz fire caused by the upload verb — links the upload trace.
        await WaitForStateAsync(batchId, GoldpathBulkBatchState.Validated, timeout.Token);
        var validateRun = await WaitForSpanAsync(spans,
            s => s.OperationName == "goldpath.job.run" && Equals(s.GetTagItem("goldpath.job"), "GoldpathBulkValidateJob`1"), timeout.Token);
        Assert.Contains(validateRun.Links, l => l.Context.TraceId == uploadTrace);

        Assert.True((await BulkAdmin.ApproveAsync(batchId, "treasurer", "four-eyes done", timeout.Token)).Ok);

        // 3) EXECUTE under the operator's own trace: the run span links the TRIGGER trace,
        //    the range spans link the UPLOAD trace — both causes stay reachable in Tempo.
        ActivityTraceId triggerTrace;
        using (var trigger = entry.StartActivity("POST /goldpath/admin/jobs/trigger"))
        {
            triggerTrace = trigger!.TraceId;
            Assert.True((await Admin.TriggerAsync(_fleet, "GoldpathBulkExecuteJob`1", dryRun: false, "it-operator", timeout.Token)).Ok);
        }

        var partial = await WaitForStateAsync(batchId, GoldpathBulkBatchState.CompletedWithFailures, timeout.Token);
        var executeRun = await WaitForSpanAsync(spans,
            s => s.OperationName == "goldpath.job.run" && Equals(s.GetTagItem("goldpath.job"), "GoldpathBulkExecuteJob`1"), timeout.Token);
        Assert.Contains(executeRun.Links, l => l.Context.TraceId == triggerTrace);

        var range = await WaitForSpanAsync(spans, s => s.OperationName == "goldpath.bulk.execute-range", timeout.Token);
        Assert.Equal(executeRun.TraceId, range.TraceId);   // run → chunk → range: ONE trace id
        Assert.Contains(range.Links, l => l.Context.TraceId == uploadTrace);
        var chunk = await WaitForSpanAsync(spans,
            s => s.OperationName == "goldpath.job.chunk" && s.SpanId == range.ParentSpanId, timeout.Token);
        Assert.Equal(executeRun.SpanId, chunk.ParentSpanId);

        // 4) The HANDLER ran inside that same trace — its downstream calls inherit it.
        var sinkTraces = Query(db => db.Sink.Select(s => s.TraceId).Distinct().ToList());
        Assert.Equal([executeRun.TraceId.ToHexString()], sinkTraces);

        // 5) REPLAY under a third trace: heal the poison, verb it, follow the chain again.
        Query(db =>
        {
            foreach (var row in db.Set<GoldpathBulkRow>().Where(r => r.BatchId == batchId && r.FailedAt != null))
            {
                row.Payload = row.Payload.Replace("FAIL", "ok", StringComparison.Ordinal);
            }

            return db.SaveChanges();
        });
        ActivityTraceId replayTrace;
        using (var replay = entry.StartActivity("POST /goldpath/admin/jobs/replay-items"))
        {
            replayTrace = replay!.TraceId;
            Assert.True((await Admin.ReplayItemsAsync(partial.RunId!.Value, "it-operator", timeout.Token)).Ok);
        }

        await WaitForStateAsync(batchId, GoldpathBulkBatchState.Completed, timeout.Token);
        var replayRun = await WaitForSpanAsync(spans, s => s.OperationName == "goldpath.job.replay", timeout.Token);
        Assert.Contains(replayRun.Links, l => l.Context.TraceId == replayTrace);
        var item = await WaitForSpanAsync(spans, s => s.OperationName == "goldpath.job.replay-item", timeout.Token);
        Assert.Equal(replayRun.TraceId, item.TraceId);
        Assert.NotNull(item.GetTagItem("goldpath.item_key"));
        var replayRow = await WaitForSpanAsync(spans, s => s.OperationName == "goldpath.bulk.replay-row", timeout.Token);
        Assert.Equal(replayRun.TraceId, replayRow.TraceId);
        Assert.Contains(replayRow.Links, l => l.Context.TraceId == uploadTrace);   // still anchored to the upload

        // The healed row's side effect carries the REPLAY trace — per-instruction correlation.
        var healedSink = Query(db => db.Sink.Single(s => s.RowNumber == 2));
        Assert.Equal(replayRun.TraceId.ToHexString(), healedSink.TraceId);
    }

    /// <summary>
    /// RFC goldpath-oracle §2.5: the admin listing's repeatable <c>definition</c> filter (contract R3) is bounded only by
    /// the request line, so a host that raises Kestrel's limit can send more than Oracle's 1,000-element <c>IN</c> list.
    /// 1,001 distinct values, one of them a real definition, still answer with exactly that definition's batches.
    /// </summary>
    [Fact]
    public async Task A_listing_filtered_by_1001_definitions_returns_the_matching_batches()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Guid batchId;
        using (var scope = _host.Services.CreateScope())
        {
            var csv = new MemoryStream(Encoding.UTF8.GetBytes("EndToEndId,Iban,Amount,Note\nL1,TR1,10,\n"));
            batchId = (await Engine.IngestAsync(scope.ServiceProvider, "payments", csv, "listing.csv", null, timeout.Token)).Batch.Id;
        }

        // The real value LAST, at position 1,001: a filter that lost its tail chunk would miss it.
        string[] definitions = [.. Enumerable.Range(1, 1_000).Select(i => $"definition-{i:D4}"), "payments"];
        Assert.Equal(1_001, definitions.Distinct(StringComparer.Ordinal).Count());

        var listed = await BulkAdmin.GetBatchesAsync(null, definitions, null, 100, timeout.Token);
        Assert.Equal(batchId, Assert.Single(listed).Id);

        // And a filter of 1,001 values naming no definition finds nothing, rather than everything.
        Assert.Empty(await BulkAdmin.GetBatchesAsync(null, [.. definitions[..^1], "definition-1001"], null, 100, timeout.Token));
    }
}

/// <summary>The bulk suite on Oracle, the clustered Quartz store on Oracle (G7 ORA-6, #409; RFC goldpath-oracle §7, Bulk).</summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class BulkOracleTests(OracleFixture oracle) : BulkTests(SuiteStore.Oracle(oracle, "GP_BULK"));
