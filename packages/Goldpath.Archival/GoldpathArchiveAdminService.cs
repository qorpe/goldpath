using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>One definition's live lifecycle numbers.</summary>
public sealed record GoldpathArchiveDefinitionStatus(string Name, long Entries, int DueBacklog, int ActiveHolds, long ChainHead, long PurgedThrough);

/// <summary>
/// The archival admin verbs (§7.1: the API is the contract). Hold and erasure verbs write
/// their OWN evidence rows (a hold row IS its audit; an erasure record IS the KVKK answer) —
/// no verb happens without a durable who/when/what.
/// </summary>
public sealed class GoldpathArchiveAdminService<TContext>
    where TContext : DbContext
{
    private readonly GoldpathArchiveEngine<TContext> _engine;
    private readonly GoldpathArchivalOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;

    /// <summary>Registered by <c>AddGoldpathArchival</c>.</summary>
    public GoldpathArchiveAdminService(GoldpathArchiveEngine<TContext> engine, GoldpathArchivalOptions options, IServiceScopeFactory scopeFactory, TimeProvider time)
    {
        _engine = engine;
        _options = options;
        _scopeFactory = scopeFactory;
        _time = time;
    }

    /// <summary>Every definition with its live numbers (the console's landing view).</summary>
    public async Task<IReadOnlyList<GoldpathArchiveDefinitionStatus>> GetDefinitionsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var result = new List<GoldpathArchiveDefinitionStatus>();
        foreach (var definition in _options.Archives)
        {
            var entries = await db.Set<GoldpathArchiveEntry>().LongCountAsync(e => e.Definition == definition.Name, ct);
            var due = await _engine.CountDueAsync(db, definition, ct);
            var holds = await db.Set<GoldpathLegalHold>().CountAsync(h => h.Definition == definition.Name && h.LiftedAt == null, ct);
            var state = await db.Set<GoldpathArchiveChainState>().AsNoTracking()
                .FirstOrDefaultAsync(s => s.Definition == definition.Name, ct);
            result.Add(new GoldpathArchiveDefinitionStatus(
                definition.Name, entries, due, holds, state?.LastIndex ?? 0, state?.PurgedThroughIndex ?? 0));
            GoldpathArchivalMetrics.SetBacklog(definition.Name, due);
        }

        return result;
    }

    /// <summary>Retrieves one archived aggregate (tenant-scoped when a tenant is supplied).</summary>
    public async Task<GoldpathArchiveEntry?> RetrieveAsync(string definition, string aggregateKey, string? tenant, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        using var scope = _scopeFactory.CreateScope();
        var entry = await _engine.RetrieveAsync(scope.ServiceProvider, definition, aggregateKey, ct);
        GoldpathArchivalMetrics.RetrievalObserved(definition, _time.GetElapsedTime(start));
        return entry is null || (tenant is not null && entry.Tenant != tenant) ? null : entry;
    }

    /// <summary>Places a legal hold — the hold row IS the audit (who/when/case).</summary>
    public async Task<GoldpathAdminResult> PlaceHoldAsync(string definition, string aggregateKey, string caseReference, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(caseReference))
        {
            return new GoldpathAdminResult(false, "a legal hold needs its case reference — that is what justifies it later");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var active = await db.Set<GoldpathLegalHold>().AnyAsync(h =>
            h.Definition == definition && h.AggregateKey == aggregateKey && h.LiftedAt == null, ct);
        if (active)
        {
            return new GoldpathAdminResult(false, "an active hold already covers this aggregate");
        }

        db.Add(new GoldpathLegalHold
        {
            Definition = definition,
            AggregateKey = aggregateKey,
            CaseReference = caseReference,
            PlacedBy = actor,
            PlacedAt = _time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return new GoldpathAdminResult(true, "hold placed");
    }

    /// <summary>Lifts a hold (the lift stamps who/when on the same row).</summary>
    public async Task<GoldpathAdminResult> LiftHoldAsync(string definition, string aggregateKey, string actor, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var hold = await db.Set<GoldpathLegalHold>().FirstOrDefaultAsync(h =>
            h.Definition == definition && h.AggregateKey == aggregateKey && h.LiftedAt == null, ct);
        if (hold is null)
        {
            return new GoldpathAdminResult(false, "no active hold on this aggregate");
        }

        hold.LiftedAt = _time.GetUtcNow();
        hold.LiftedBy = actor;
        await db.SaveChangesAsync(ct);
        return new GoldpathAdminResult(true, "hold lifted");
    }

    /// <summary>Active (and optionally lifted) holds, newest first.</summary>
    public async Task<IReadOnlyList<GoldpathLegalHold>> GetHoldsAsync(bool includeLifted, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathLegalHold>().AsNoTracking()
            .Where(h => includeLifted || h.LiftedAt == null)
            .OrderByDescending(h => h.PlacedAt)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The KVKK/GDPR verb (D4): redacts every classified field INSIDE the stored document
    /// via the DataProtection catalog, re-stamps the content hash, marks the entry erased
    /// and writes the evidence row. The chain hash never changes — verification reads the
    /// divergence WITH the erasure mark as lawful, WITHOUT it as tamper. Held entries refuse.
    /// </summary>
    public async Task<GoldpathAdminResult> EraseAsync(string definition, string aggregateKey, string subjectKey, string actor, string? detail, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subjectKey))
        {
            // The evidence row is the answer to "whose data was erased"; a blank one answers nothing, and Oracle refuses
            // '' outright (#476), so the verb refuses it the same on every provider.
            return new GoldpathAdminResult(false, "an erasure needs the data subject's key — the evidence record names whose data was erased");
        }

        using var scope = _scopeFactory.CreateScope();
        var protector = scope.ServiceProvider.GetService<IGoldpathDataProtector>();
        if (protector is null)
        {
            return new GoldpathAdminResult(false, "erasure needs the DataProtection module — classification is what tells the archive WHAT to redact (GP1401)");
        }

        var archiveDefinition = _options.Archives.FirstOrDefault(a => a.Name == definition);
        if (archiveDefinition is null)
        {
            return new GoldpathAdminResult(false, $"no archive definition '{definition}'");
        }

        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var entry = await db.Set<GoldpathArchiveEntry>().FirstOrDefaultAsync(e =>
            e.Definition == definition && e.AggregateKey == aggregateKey, ct);
        if (entry is null)
        {
            return new GoldpathAdminResult(false, "no such archive entry");
        }

        var held = await db.Set<GoldpathLegalHold>().AnyAsync(h =>
            h.Definition == definition && h.AggregateKey == aggregateKey && h.LiftedAt == null, ct);
        if (held)
        {
            return new GoldpathAdminResult(false, "an active legal hold exempts this entry from erasure (D4) — lift the hold first, with counsel");
        }

        var root = JsonSerializer.Deserialize(entry.Document, archiveDefinition.RootType, GoldpathArchiveEnvelope.Json);
        if (root is null)
        {
            return new GoldpathAdminResult(false, "the stored document does not deserialize — run verification before erasing");
        }

        var redacted = GoldpathDocumentRedactor.Redact(root, protector);
        if (redacted == 0)
        {
            return new GoldpathAdminResult(false, "no classified fields in this document — nothing to erase (check the classification catalog)");
        }

        entry.Document = JsonSerializer.Serialize(root, archiveDefinition.RootType, GoldpathArchiveEnvelope.Json);
        entry.PreErasureContentHash ??= entry.ContentHash;
        entry.ContentHash = GoldpathArchiveEnvelope.ContentHash(
            entry.SchemaVersion, entry.Definition, entry.AggregateKey, entry.Tenant, entry.DueAt, entry.Document);
        entry.ErasedAt = _time.GetUtcNow();

        db.Add(new GoldpathErasureRecord
        {
            SubjectKey = subjectKey,
            RequestedBy = actor,
            RequestedAt = entry.ErasedAt.Value,
            EntriesAffected = 1,
            Detail = detail ?? $"{definition}/{aggregateKey}: {redacted} fields redacted",
        });
        await db.SaveChangesAsync(ct);
        GoldpathArchivalMetrics.Erased(definition);
        return new GoldpathAdminResult(true, $"{redacted} classified fields redacted; evidence recorded");
    }

    /// <summary>The erasure evidence trail (the KVKK answer is a query, not a search).</summary>
    public async Task<IReadOnlyList<GoldpathErasureRecord>> GetErasuresAsync(int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathErasureRecord>().AsNoTracking()
            .OrderByDescending(e => e.RequestedAt)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    /// <summary>On-demand chain verification of one definition (the scheduled job stays the watch).</summary>
    public async Task<IReadOnlyList<GoldpathChainFinding>> VerifyAsync(string definition, CancellationToken ct)
    {
        var archiveDefinition = _options.Archives.FirstOrDefault(a => a.Name == definition);
        if (archiveDefinition is null)
        {
            return [];
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var state = await db.Set<GoldpathArchiveChainState>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Definition == definition, ct);
        if (state is null)
        {
            return [];
        }

        var findings = await _engine.VerifySliceAsync(
            scope.ServiceProvider, archiveDefinition, state.PurgedThroughIndex + 1, state.LastIndex, ct);
        if (findings.Count > 0)
        {
            GoldpathArchivalMetrics.VerifyFailures(definition, findings.Count);
        }

        return findings;
    }
}
