using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>One definition's live intake numbers (the console's landing view).</summary>
public sealed record GoldpathBulkDefinitionStatus(
    string Name,
    IReadOnlyDictionary<string, int> BatchesByState,
    int AwaitingApproval,
    double? OldestAwaitingApprovalSeconds);

/// <summary>One batch over the wire: the state machine's public face.</summary>
public sealed record GoldpathBulkBatchInfo(
    Guid Id,
    string Definition,
    string State,
    string? Tenant,
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int ExecutedRows,
    int FailedRows,
    Guid? RunId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    string? DecisionNote,
    DateTimeOffset? CompletedAt);

/// <summary>
/// The bulk admin verbs (§7.1: the API is the contract — no screen action exists that curl
/// cannot do). The state machine's rows ARE the audit: upload stamps the batch, the gate
/// stamps the actor and the note, execution stamps the run — every verb leaves durable
/// who/when/what without a separate audit table.
/// </summary>
public sealed class GoldpathBulkAdminService<TContext>
    where TContext : DbContext
{
    private readonly GoldpathBulkEngine<TContext> _engine;
    private readonly GoldpathBulkOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    /// <summary>Registered by <c>AddGoldpathBulk</c>.</summary>
    public GoldpathBulkAdminService(
        GoldpathBulkEngine<TContext> engine, GoldpathBulkOptions options,
        IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _engine = engine;
        _options = options;
        _scopeFactory = scopeFactory;
        _time = time;
    }

    /// <summary>
    /// Uploads a file into a definition and, when the jobs admin surface is present, fires
    /// the validate run immediately — the cron stays the safety net, the operator never
    /// waits for it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is null, empty or whitespace (#476).</exception>
    public async Task<GoldpathBulkBatchInfo> UploadAsync(
        string definition, Stream content, string fileName, string? tenant, string actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);   // #476: the file's name is a NOT NULL column, and '' is NULL on Oracle
        using var scope = _scopeFactory.CreateScope();
        var (batch, created) = await _engine.IngestAsync(scope.ServiceProvider, definition, content, fileName, tenant, ct);
        if (created)
        {
            var admin = scope.ServiceProvider.GetService<GoldpathJobsAdminService<TContext>>();
            if (admin is not null)
            {
                var options = scope.ServiceProvider.GetRequiredService<GoldpathJobsOptions>();
                var validateJob = options.Jobs.FirstOrDefault(j => j.JobType.Name.StartsWith("GoldpathBulkValidateJob", StringComparison.Ordinal));
                if (validateJob is not null)
                {
                    // Best effort: a refused trigger (fleet still booting) is NOT an upload failure.
                    await admin.TriggerAsync(options.SchedulerName, validateJob.Name, dryRun: false, actor, ct);
                }
            }
        }

        return ToInfo(batch);
    }

    /// <summary>Every definition with its live intake numbers; feeds the state gauges.</summary>
    public async Task<IReadOnlyList<GoldpathBulkDefinitionStatus>> GetDefinitionsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var result = new List<GoldpathBulkDefinitionStatus>();
        foreach (var definition in _options.Batches)
        {
            var counts = await db.Set<GoldpathBulkBatch>().AsNoTracking()
                .Where(b => b.Definition == definition.Name)
                .GroupBy(b => b.State)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var oldestAwaiting = await db.Set<GoldpathBulkBatch>().AsNoTracking()
                .Where(b => b.Definition == definition.Name && b.State == GoldpathBulkBatchState.Validated)
                .OrderBy(b => b.ValidatedAt)
                .Select(b => b.ValidatedAt)
                .FirstOrDefaultAsync(ct);

            var byState = counts.ToDictionary(c => c.Key.ToString(), c => c.Count, StringComparer.Ordinal);
            var awaiting = byState.GetValueOrDefault(nameof(GoldpathBulkBatchState.Validated));
            double? oldestSeconds = oldestAwaiting is { } t && awaiting > 0 ? (now - t).TotalSeconds : null;
            result.Add(new GoldpathBulkDefinitionStatus(definition.Name, byState, awaiting, oldestSeconds));
            GoldpathBulkMetrics.SetIntakeSnapshot(definition.Name, byState, oldestSeconds ?? 0);
        }

        return result;
    }

    private static List<GoldpathBulkBatchState> ParseStates(string[]? raw)
        => raw is null
            ? []
            : [.. raw.Select(value => Enum.TryParse<GoldpathBulkBatchState>(value, ignoreCase: true, out var parsed) ? parsed : (GoldpathBulkBatchState?)null)
                     .Where(parsed => parsed is not null)
                     .Select(parsed => parsed!.Value)];

    /// <summary>Recent batches, newest first (optionally by state/definition; tenant-scoped when supplied).</summary>
    public async Task<IReadOnlyList<GoldpathBulkBatchInfo>> GetBatchesAsync(
        string[]? state, string[]? definition, string? tenant, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var query = db.Set<GoldpathBulkBatch>().AsNoTracking();
        var states = ParseStates(state);
        if (states.Count > 0)
        {
            // Several states are OR'd (contract R3); one behaves exactly as R2 did.
            query = query.Where(b => states.Contains(b.State));
        }

        if (definition is { Length: > 0 })
        {
            // Bounded only by the request line, so it can pass Oracle's 1,000-element IN list (RFC goldpath-oracle §2.5).
            query = query.ChunkedContains(b => b.Definition, definition, db.Database.ProviderName);
        }

        if (tenant is not null)
        {
            query = query.Where(b => b.Tenant == tenant);
        }

        var batches = await query.OrderByDescending(b => b.ReceivedAt).Take(AdminPaging.Clamp(take)).ToListAsync(ct);
        return [.. batches.Select(ToInfo)];
    }

    /// <summary>One batch's full story (tenant-scoped: a foreign tenant sees nothing — fail closed).</summary>
    public async Task<GoldpathBulkBatchInfo?> GetBatchAsync(Guid batchId, string? tenant, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var batch = await db.Set<GoldpathBulkBatch>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == batchId, ct);
        return batch is null || (tenant is not null && batch.Tenant != tenant) ? null : ToInfo(batch);
    }

    /// <summary>The validation report, pageable by row number (value-free by construction).</summary>
    public async Task<IReadOnlyList<GoldpathBulkRowError>> GetErrorsAsync(
        Guid batchId, string? tenant, int afterRowNumber, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        if (tenant is not null
            && !await db.Set<GoldpathBulkBatch>().AsNoTracking().AnyAsync(b => b.Id == batchId && b.Tenant == tenant, ct))
        {
            // R1: a foreign batch id answers like an absent one — no cross-tenant probing.
            return [];
        }

        return await db.Set<GoldpathBulkRowError>().AsNoTracking()
            .Where(e => e.BatchId == batchId && e.RowNumber > afterRowNumber)
            .OrderBy(e => e.RowNumber).ThenBy(e => e.Id)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    /// <summary>The gate's YES (engine-guarded; the batch row records who/when/why).</summary>
    public async Task<GoldpathAdminResult> ApproveAsync(Guid batchId, string actor, string? note, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        return await _engine.ApproveAsync(scope.ServiceProvider, batchId, actor, note, ct);
    }

    /// <summary>The gate's NO (the note is mandatory — it is the evidence).</summary>
    public async Task<GoldpathAdminResult> RejectAsync(Guid batchId, string actor, string? note, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        return await _engine.RejectAsync(scope.ServiceProvider, batchId, actor, note, ct);
    }

    private static GoldpathBulkBatchInfo ToInfo(GoldpathBulkBatch b) => new(
        b.Id, b.Definition, b.State.ToString(), b.Tenant,
        b.TotalRows, b.ValidRows, b.InvalidRows, b.ExecutedRows, b.FailedRows,
        b.RunId, b.ReceivedAt, b.ValidatedAt, b.DecidedAt, b.DecidedBy, b.DecisionNote, b.CompletedAt);
}
