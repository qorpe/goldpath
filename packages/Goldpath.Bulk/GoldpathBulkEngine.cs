using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The intake engine (bulk RFC §2): ingest with content-hash dedup, chunk-resumable
/// validation, the approval gate, and row execution with claim-before-side-effect
/// semantics. The engine owns STATE; scheduling and recovery belong to Goldpath.Jobs.
/// </summary>
public sealed class GoldpathBulkEngine<TContext>
    where TContext : DbContext
{
    private readonly GoldpathBulkOptions _options;
    private readonly GoldpathBulkFileStore<TContext> _store;
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathBulkEngine<TContext>> _logger;

    /// <summary>Creates the engine.</summary>
    public GoldpathBulkEngine(
        GoldpathBulkOptions options,
        GoldpathBulkFileStore<TContext> store,
        TimeProvider time,
        ILogger<GoldpathBulkEngine<TContext>> logger)
    {
        _options = options;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Stores the file and opens a batch. Identical bytes with an open (non-rejected)
    /// batch for the same definition and tenant return THAT batch — a client retry storm
    /// cannot create a double-payment risk (D1). A REJECTED file may be resubmitted
    /// deliberately: rejection was a human decision, resubmission is another one.
    /// </summary>
    public async Task<(GoldpathBulkBatch Batch, bool Created)> IngestAsync(
        IServiceProvider services, string definitionName, Stream content, string fileName,
        string? tenant, CancellationToken cancellationToken)
    {
        var definition = _options.Definition(definitionName);
        var db = services.GetRequiredService<TContext>();
        var (file, _) = await _store.SaveAsync(db, content, fileName, cancellationToken);

        var existing = await db.Set<GoldpathBulkBatch>().AsNoTracking()
            .Where(b => b.Definition == definition.Name && b.FileId == file.Id && b.Tenant == tenant)
            .Where(b => b.State != GoldpathBulkBatchState.Rejected)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            GoldpathBulkMetrics.DedupHit(definition.Name);
            return (existing, false);
        }

        var batch = new GoldpathBulkBatch
        {
            Id = Guid.NewGuid(),
            Definition = definition.Name,
            FileId = file.Id,
            State = GoldpathBulkBatchState.Received,
            Tenant = tenant,
            ReceivedAt = _time.GetUtcNow(),
            // The upload request's trace — the anchor every later span links back to.
            TraceParent = Activity.Current?.Id,
        };
        db.Set<GoldpathBulkBatch>().Add(batch);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Bulk batch {BatchId} received for definition {Definition} ({FileName}).", batch.Id, definition.Name, fileName);
        return (batch, true);
    }

    /// <summary>
    /// Parses and validates one batch end to end (the validate job's chunk). Idempotent by
    /// wipe-and-rewrite: a resumed chunk starts the report over — validation has no side
    /// effects outside these tables, so redo is the honest resume.
    /// </summary>
    public async Task ValidateBatchAsync(IServiceProvider services, Guid batchId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var batch = await db.Set<GoldpathBulkBatch>().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null || batch.State is not (GoldpathBulkBatchState.Received or GoldpathBulkBatchState.Validating))
        {
            return;   // decided elsewhere; a stale plan is not an error
        }

        // Child of the validate run's chunk span, LINKED to the upload trace.
        using var activity = GoldpathBulkDiagnostics.Source.StartActivity(
            "goldpath.bulk.validate", ActivityKind.Internal, default(ActivityContext), links: TraceLink.To(batch.TraceParent));
        activity?.SetTag("goldpath.batch_id", batch.Id);
        activity?.SetTag("goldpath.definition", batch.Definition);

        var definition = _options.Definition(batch.Definition);
        if (batch.State == GoldpathBulkBatchState.Received)
        {
            batch.State = GoldpathBulkBatchState.Validating;
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Set<GoldpathBulkRow>().Where(r => r.BatchId == batch.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Set<GoldpathBulkRowError>().Where(e => e.BatchId == batch.Id).ExecuteDeleteAsync(cancellationToken);

        var started = _time.GetUtcNow();
        var duplicates = new GoldpathBulkDuplicateKeys();
        var pendingRows = new List<GoldpathBulkRow>();
        var pendingErrors = new List<GoldpathBulkRowError>();
        int total = 0, valid = 0, invalid = 0;
        var ceilingHit = false;

        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task FlushAsync()
        {
            if (pendingRows.Count == 0 && pendingErrors.Count == 0)
            {
                return;
            }

            db.Set<GoldpathBulkRow>().AddRange(pendingRows);
            db.Set<GoldpathBulkRowError>().AddRange(pendingErrors);
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            db.Attach(batch);
            pendingRows.Clear();
            pendingErrors.Clear();
        }

        // The key a staged row was recorded under, re-derived from its payload: still in this
        // flush's buffer, or already written. Only a row that recorded a key is ever asked for,
        // and only valid rows record one, so the row is always staged.
        async ValueTask<string?> KeyOfRowAsync(int rowNumber)
        {
            var payload = pendingRows.Find(r => r.RowNumber == rowNumber)?.Payload
                ?? await db.Set<GoldpathBulkRow>().AsNoTracking()
                    .Where(r => r.BatchId == batch.Id && r.RowNumber == rowNumber)
                    .Select(r => r.Payload)
                    .SingleAsync(cancellationToken);
            return definition.KeyOfPayload(payload);
        }

        try
        {
            await using var stream = _store.OpenRead(db, batch.FileId);
            total = await definition.Format.ReadAsync(
                stream,
                async raw =>
                {
                    if (raw.RowNumber > definition.MaxRows)
                    {
                        ceilingHit = true;
                        await ceiling.CancelAsync();
                        return;
                    }

                    var result = definition.ValidateRow(raw, services);
                    var errors = result.Errors;
                    if (errors.Count == 0 && result.RowKey is { } key
                        && await duplicates.FirstRowOfAsync(key, raw.RowNumber, KeyOfRowAsync) is { } first)
                    {
                        errors = [("(row key)", $"duplicate of row {first} within the file")];
                    }

                    if (errors.Count == 0)
                    {
                        valid++;
                        pendingRows.Add(new GoldpathBulkRow { BatchId = batch.Id, RowNumber = raw.RowNumber, Payload = result.Payload! });
                    }
                    else
                    {
                        invalid++;
                        foreach (var (field, message) in errors)
                        {
                            pendingErrors.Add(new GoldpathBulkRowError { BatchId = batch.Id, RowNumber = raw.RowNumber, Field = field, Message = message });
                        }
                    }

                    if (pendingRows.Count + pendingErrors.Count >= _options.InsertBatchSize)
                    {
                        await FlushAsync();
                    }
                },
                error =>
                {
                    invalid++;
                    pendingErrors.Add(new GoldpathBulkRowError { BatchId = batch.Id, RowNumber = error.RowNumber, Field = "(line)", Message = error.Message });
                    return Task.CompletedTask;
                },
                ceiling.Token);
        }
        catch (OperationCanceledException) when (ceilingHit && !cancellationToken.IsCancellationRequested)
        {
            // The ceiling aborted the read on purpose; the refusal below is the outcome.
        }

        if (ceilingHit)
        {
            await db.Set<GoldpathBulkRow>().Where(r => r.BatchId == batch.Id).ExecuteDeleteAsync(cancellationToken);
            db.Set<GoldpathBulkRowError>().Add(new GoldpathBulkRowError
            {
                BatchId = batch.Id,
                RowNumber = 0,
                Field = "(file)",
                Message = $"row count exceeds the definition's ceiling ({definition.MaxRows}) — the batch is refused whole; split the file",
            });
            batch.State = GoldpathBulkBatchState.Rejected;
            batch.DecidedAt = _time.GetUtcNow();
            batch.DecidedBy = "goldpath";
            batch.DecisionNote = "row ceiling exceeded";
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Bulk batch {BatchId} refused: row ceiling {MaxRows} exceeded.", batch.Id, definition.MaxRows);
            return;
        }

        await FlushAsync();
        batch.TotalRows = total;
        batch.ValidRows = valid;
        batch.InvalidRows = invalid;
        batch.ValidatedAt = _time.GetUtcNow();
        if (definition.AutoApprove)
        {
            batch.State = GoldpathBulkBatchState.Approved;
            batch.DecidedAt = batch.ValidatedAt;
            batch.DecidedBy = "goldpath:auto-approve";
        }
        else
        {
            batch.State = GoldpathBulkBatchState.Validated;
        }

        await db.SaveChangesAsync(cancellationToken);
        GoldpathBulkMetrics.Validated(definition.Name, valid, invalid, (_time.GetUtcNow() - started).TotalSeconds);
        _logger.LogInformation(
            "Bulk batch {BatchId} validated: {Valid} valid, {Invalid} invalid of {Total} rows.",
            batch.Id, valid, invalid, total);
    }

    /// <summary>
    /// The gate's YES (D2/D5): refuses non-validated states, and refuses invalid rows
    /// unless the definition tolerates partial execution. The actor is the evidence.
    /// </summary>
    public async Task<GoldpathAdminResult> ApproveAsync(
        IServiceProvider services, Guid batchId, string actor, string? note, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var batch = await db.Set<GoldpathBulkBatch>().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return new GoldpathAdminResult(false, "no such batch");
        }

        if (batch.State != GoldpathBulkBatchState.Validated)
        {
            return new GoldpathAdminResult(false, $"only a Validated batch can be approved — this one is {batch.State}");
        }

        var definition = _options.Definition(batch.Definition);
        if (batch.InvalidRows > 0 && !definition.TolerateInvalidRows)
        {
            return new GoldpathAdminResult(false,
                $"{batch.InvalidRows} invalid rows block approval — fix the file and re-upload, or opt the definition into TolerateInvalidRows");
        }

        batch.State = GoldpathBulkBatchState.Approved;
        batch.DecidedAt = _time.GetUtcNow();
        batch.DecidedBy = actor;
        batch.DecisionNote = note;
        await db.SaveChangesAsync(cancellationToken);
        return new GoldpathAdminResult(true, batch.InvalidRows > 0
            ? $"approved: the {batch.ValidRows} valid rows will execute; the report records the {batch.InvalidRows} skipped"
            : "approved");
    }

    /// <summary>The gate's NO: a rejection without a reason teaches nothing — the note is mandatory.</summary>
    public async Task<GoldpathAdminResult> RejectAsync(
        IServiceProvider services, Guid batchId, string actor, string? note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return new GoldpathAdminResult(false, "a rejection needs a reason — the note is the evidence the next uploader reads");
        }

        var db = services.GetRequiredService<TContext>();
        var batch = await db.Set<GoldpathBulkBatch>().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return new GoldpathAdminResult(false, "no such batch");
        }

        if (batch.State != GoldpathBulkBatchState.Validated)
        {
            return new GoldpathAdminResult(false, $"only a Validated batch can be rejected — this one is {batch.State}");
        }

        batch.State = GoldpathBulkBatchState.Rejected;
        batch.DecidedAt = _time.GetUtcNow();
        batch.DecidedBy = actor;
        batch.DecisionNote = note;
        await db.SaveChangesAsync(cancellationToken);
        return new GoldpathAdminResult(true, "rejected");
    }

    /// <summary>
    /// Claims batches for an execute run: Approved ones, plus Executing orphans whose run
    /// ended without completing them (takeover after a permanently failed run — the stuck-
    /// batch runbook's automatic half). Returns what the run should plan.
    /// </summary>
    public async Task<IReadOnlyList<GoldpathBulkBatch>> AdoptForExecutionAsync(
        IServiceProvider services, Guid runId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var candidates = await db.Set<GoldpathBulkBatch>()
            .Where(b => b.State == GoldpathBulkBatchState.Approved || b.State == GoldpathBulkBatchState.Executing)
            .OrderBy(b => b.ReceivedAt)
            .ToListAsync(cancellationToken);

        var adopted = new List<GoldpathBulkBatch>();
        foreach (var batch in candidates)
        {
            if (batch.State == GoldpathBulkBatchState.Executing)
            {
                if (batch.RunId == runId)
                {
                    adopted.Add(batch);   // our own resumed run
                    continue;
                }

                var stillRunning = batch.RunId is { } previous && await db.Set<GoldpathJobRun>().AsNoTracking()
                    .AnyAsync(r => r.Id == previous && r.Status == GoldpathJobRunStatus.Running, cancellationToken);
                if (stillRunning)
                {
                    continue;   // another run owns it
                }
            }

            batch.State = GoldpathBulkBatchState.Executing;
            batch.RunId = runId;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                adopted.Add(batch);
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();   // raced by a parallel plan; the winner plans it
            }
        }

        return adopted;
    }

    /// <summary>
    /// Executes one row range of one batch (the execute job's chunk). Fresh rows are
    /// CLAIMED (persisted) before any handler runs; rows found already claimed but never
    /// stamped were interrupted mid-flight — they go to the repair queue instead of being
    /// silently re-sent (MDM constraint 2). Row stamps and batch counters write BATCHED
    /// (constraint 4).
    /// </summary>
    public async Task ExecuteRangeAsync(
        IServiceProvider services, GoldpathJobChunk chunk, Guid batchId, long offset, long endExclusive,
        CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var batch = await db.Set<GoldpathBulkBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
        if (batch is null || batch.State != GoldpathBulkBatchState.Executing)
        {
            return;   // stale plan; the state machine moved on
        }

        // Child of the execute run's chunk span, LINKED to the upload trace: the handler
        // (and its downstream HttpClient calls) run under this span via Activity.Current.
        using var activity = GoldpathBulkDiagnostics.Source.StartActivity(
            "goldpath.bulk.execute-range", ActivityKind.Internal, default(ActivityContext), links: TraceLink.To(batch.TraceParent));
        activity?.SetTag("goldpath.batch_id", batch.Id);
        activity?.SetTag("goldpath.definition", batch.Definition);
        activity?.SetTag("goldpath.range", $"{offset}:{endExclusive}");

        // The range is over ROW NUMBER VALUES (keyset over the PK, no offset paging):
        // invalid rows leave gaps, so chunks are bounded-above, not exactly equal — fine.
        var definition = _options.Definition(batch.Definition);
        var rows = await db.Set<GoldpathBulkRow>()
            .Where(r => r.BatchId == batchId && r.RowNumber >= offset && r.RowNumber < endExclusive)
            .Where(r => r.ExecutedAt == null && r.FailedAt == null)
            .OrderBy(r => r.RowNumber)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await TryCompleteAsync(db, batchId, cancellationToken);
            return;
        }

        var now = _time.GetUtcNow();
        int executed = 0, failed = 0;

        // Interrupted rows: claimed by an earlier attempt, never stamped. Repair, don't re-send.
        foreach (var row in rows.Where(r => r.ClaimedAt is not null))
        {
            row.FailedAt = now;
            failed++;
            chunk.ReportItemFailure(ItemKey(batchId, row.RowNumber),
                "interrupted mid-flight on a previous attempt — confirm the downstream state, then replay");
        }

        // THE claim: persisted before any side effect.
        var fresh = rows.Where(r => r.ClaimedAt is null).ToList();
        foreach (var row in fresh)
        {
            row.ClaimedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var row in fresh)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var context = new GoldpathBulkRowContext(batchId, row.RowNumber, batch.Tenant, replay: false, services);
            try
            {
                await definition.ExecuteRow(row.Payload, context, cancellationToken);
                row.ExecutedAt = _time.GetUtcNow();
                executed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // shutdown: the claim stays; the resumed attempt repairs it honestly
            }
            catch (Exception e)
            {
                row.FailedAt = _time.GetUtcNow();
                failed++;
                chunk.ReportItemFailure(ItemKey(batchId, row.RowNumber), e.Message);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.Set<GoldpathBulkBatch>().Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ExecutedRows, b => b.ExecutedRows + executed)
                .SetProperty(b => b.FailedRows, b => b.FailedRows + failed), cancellationToken);
        GoldpathBulkMetrics.Executed(definition.Name, executed, failed);
        await TryCompleteAsync(db, batchId, cancellationToken);
    }

    /// <summary>
    /// Replays one repair-queue row (the jobs `replay-items` verb lands here). Success
    /// stamps the row, fixes the counters, and — when the last failure clears — flips the
    /// batch to Completed.
    /// </summary>
    public async Task ReplayRowAsync(IServiceProvider services, string itemKey, CancellationToken cancellationToken)
    {
        var (batchId, rowNumber) = ParseItemKey(itemKey);
        var db = services.GetRequiredService<TContext>();
        var row = await db.Set<GoldpathBulkRow>().FirstOrDefaultAsync(
            r => r.BatchId == batchId && r.RowNumber == rowNumber, cancellationToken);
        if (row is null)
        {
            throw new InvalidOperationException($"No bulk row for repair item '{itemKey}' — was the batch wiped?");
        }

        if (row.ExecutedAt is not null)
        {
            return;   // already done; replay is idempotent evidence, not a re-send
        }

        var batch = await db.Set<GoldpathBulkBatch>().AsNoTracking().FirstAsync(b => b.Id == batchId, cancellationToken);
        var definition = _options.Definition(batch.Definition);

        // Child of the replay-item span (operator's trace), LINKED to the upload trace —
        // the repair path is where per-instruction correlation earns its keep.
        using var activity = GoldpathBulkDiagnostics.Source.StartActivity(
            "goldpath.bulk.replay-row", ActivityKind.Internal, default(ActivityContext), links: TraceLink.To(batch.TraceParent));
        activity?.SetTag("goldpath.batch_id", batchId);
        activity?.SetTag("goldpath.row", rowNumber);

        var context = new GoldpathBulkRowContext(batchId, rowNumber, batch.Tenant, replay: true, services);
        await definition.ExecuteRow(row.Payload, context, cancellationToken);

        row.ExecutedAt = _time.GetUtcNow();
        row.FailedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await db.Set<GoldpathBulkBatch>().Where(b => b.Id == batchId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ExecutedRows, b => b.ExecutedRows + 1)
                .SetProperty(b => b.FailedRows, b => b.FailedRows - 1), cancellationToken);
        await TryCompleteAsync(db, batchId, cancellationToken);
    }

    /// <summary>Deletes raw file bytes whose retention elapsed (terminal batches only, D6).</summary>
    public async Task<int> PurgeExpiredFilesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var purged = 0;
        foreach (var definition in _options.Batches.Where(d => d.DeleteFileAfter is not null))
        {
            var cutoff = now - definition.DeleteFileAfter!.Value;
            var expired = await db.Set<GoldpathBulkBatch>().AsNoTracking()
                .Where(b => b.Definition == definition.Name)
                .Where(b => b.State == GoldpathBulkBatchState.Rejected
                    || b.State == GoldpathBulkBatchState.Completed
                    || b.State == GoldpathBulkBatchState.CompletedWithFailures)
                .Where(b => b.DecidedAt < cutoff || b.CompletedAt < cutoff)
                .Select(b => b.FileId)
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var fileId in expired)
            {
                if (await db.Set<GoldpathBulkFile>().AnyAsync(f => f.Id == fileId, cancellationToken))
                {
                    await _store.DeleteAsync(db, fileId, cancellationToken);
                    purged++;
                }
            }
        }

        return purged;
    }

    private async Task TryCompleteAsync(TContext db, Guid batchId, CancellationToken cancellationToken)
    {
        var open = await db.Set<GoldpathBulkRow>()
            .CountAsync(r => r.BatchId == batchId && r.ExecutedAt == null && r.FailedAt == null, cancellationToken);
        if (open > 0)
        {
            return;
        }

        db.ChangeTracker.Clear();
        var batch = await db.Set<GoldpathBulkBatch>().FirstAsync(b => b.Id == batchId, cancellationToken);
        var terminal = batch.State is GoldpathBulkBatchState.Completed or GoldpathBulkBatchState.CompletedWithFailures;
        var failures = await db.Set<GoldpathBulkRow>().CountAsync(r => r.BatchId == batchId && r.FailedAt != null, cancellationToken);
        var target = failures == 0 ? GoldpathBulkBatchState.Completed : GoldpathBulkBatchState.CompletedWithFailures;
        if (batch.State == target || (terminal && failures > 0))
        {
            return;
        }

        batch.State = target;
        batch.CompletedAt ??= _time.GetUtcNow();

        // The terminal flip writes AUTHORITATIVE counters from the row stamps: a kill-9
        // landing between a chunk's stamp save and its counter increment would otherwise
        // leave the ledger permanently one short (Executed+Failed != ValidRows) — the
        // money right, the books wrong. The rows are the truth; the counters its cache.
        // (Found by the two-executor kill-9 proof on CI hardware.)
        batch.ExecutedRows = await db.Set<GoldpathBulkRow>()
            .CountAsync(r => r.BatchId == batchId && r.ExecutedAt != null, cancellationToken);
        batch.FailedRows = failures;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Bulk batch {BatchId} finished as {State}.", batchId, target);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();   // a parallel chunk got there first — same outcome
        }
    }

    /// <summary>Builds the repair-queue item key of one row — a WIRE CONTRACT (ops scripts parse it).</summary>
    public static string ItemKey(Guid batchId, int rowNumber)
        => string.Create(CultureInfo.InvariantCulture, $"{batchId:N}#{rowNumber}");

    /// <summary>Parses an <see cref="ItemKey"/> back into its coordinates.</summary>
    public static (Guid BatchId, int RowNumber) ParseItemKey(string itemKey)
    {
        var separator = itemKey.IndexOf('#');
        return (Guid.ParseExact(itemKey[..separator], "N"),
            int.Parse(itemKey[(separator + 1)..], CultureInfo.InvariantCulture));
    }
}
