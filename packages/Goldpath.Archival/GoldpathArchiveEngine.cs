using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>Envelope version — never guess forward (a v2 reader will refuse v3 documents).</summary>
public static class GoldpathArchiveEnvelope
{
    /// <summary>Current envelope schema version.</summary>
    public const int SchemaVersion = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        WriteIndented = false,
    };

    /// <summary>Canonical content hash: version|definition|key|tenant|dueAt|document.</summary>
    public static string ContentHash(int schemaVersion, string definition, string key, string? tenant, DateTimeOffset dueAt, string document)
        => Sha256($"{schemaVersion}|{definition}|{key}|{tenant}|{dueAt.UtcTicks}|{document}");

    /// <summary>
    /// The <c>DueAt</c> an entry is written with: truncated to whole microseconds (#383). The
    /// value is hashed at write and re-hashed at verify from what the store returns, so it must
    /// be one every supported store holds exactly. PostgreSQL's <c>timestamptz</c> keeps
    /// microseconds and drops the 100 ns tick a <see cref="DateTimeOffset"/> can carry, so an
    /// entry hashed at full ticks there could never verify. Truncating here, and not inside
    /// <see cref="ContentHash"/>, keeps every entry already written verifying exactly as before:
    /// one stored with full ticks (SQL Server, Oracle) is still re-hashed at the ticks it holds.
    /// </summary>
    internal static DateTimeOffset StoredPrecision(DateTimeOffset dueAt)
        => dueAt.AddTicks(-(dueAt.UtcTicks % TimeSpan.TicksPerMicrosecond));

    /// <summary>Chain hash sealed at append: contentHashAtAppend|previousChainHash|index.</summary>
    public static string ChainHash(string contentHashAtAppend, string previousChainHash, long chainIndex)
        => Sha256($"{contentHashAtAppend}|{previousChainHash}|{chainIndex}");

    private static string Sha256(string canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

/// <summary>One verification finding (a broken link or a mutated document).</summary>
public sealed record GoldpathChainFinding(string Definition, long ChainIndex, string AggregateKey, string Problem);

/// <summary>
/// The archival engine: discovery → extraction → tamper-evident append (one batched write
/// per chunk), entry retention purge with legal-hold exemption, and chain verification.
/// Runs under the Jobs runner — chunking, checkpoints and resume come from there (D3).
/// </summary>
public sealed class GoldpathArchiveEngine<TContext>
    where TContext : DbContext
{
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathArchiveEngine<TContext>> _logger;

    /// <summary>Registered by <c>AddGoldpathArchival</c>.</summary>
    public GoldpathArchiveEngine(TimeProvider time, ILogger<GoldpathArchiveEngine<TContext>> logger)
    {
        _time = time;
        _logger = logger;
    }

    /// <summary>Due aggregates not yet archived (planning input).</summary>
    public async Task<int> CountDueAsync(TContext db, GoldpathArchiveDefinition definition, CancellationToken ct)
    {
        var cutoff = _time.GetUtcNow() - definition.ArchiveAfter;
        var due = await definition.CountDueAsync(db, cutoff, ct);
        if (definition.DeleteHotRows)
        {
            return due;   // archived aggregates left the hot table — due IS the backlog
        }

        var archived = await db.Set<GoldpathArchiveEntry>().CountAsync(e => e.Definition == definition.Name, ct);
        return Math.Max(0, due - archived);
    }

    /// <summary>
    /// Archives the next batch: extract graphs, append entries to the chain, optionally
    /// remove hot rows — ONE SaveChanges (atomic per chunk). Returns how many moved.
    /// </summary>
    public async Task<int> ArchiveNextBatchAsync(IServiceProvider services, GoldpathArchiveDefinition definition, int batchSize, CancellationToken ct)
    {
        var db = services.GetRequiredService<TContext>();
        var cutoff = _time.GetUtcNow() - definition.ArchiveAfter;

        // Page due keys, skipping the already-archived (moves shrink the due set on their
        // own; copies advance through the archived prefix).
        var fresh = new List<string>();
        var skip = 0;
        while (fresh.Count < batchSize)
        {
            var keys = await definition.DiscoverDueKeysAsync(db, cutoff, skip, batchSize * 2, ct);
            if (keys.Count == 0)
            {
                break;
            }

            var archived = await db.Set<GoldpathArchiveEntry>()
                .Where(e => e.Definition == definition.Name && keys.Contains(e.AggregateKey))
                .Select(e => e.AggregateKey)
                .ToListAsync(ct);
            fresh.AddRange(keys.Except(archived, StringComparer.Ordinal).Take(batchSize - fresh.Count));
            skip += keys.Count;
        }

        if (fresh.Count == 0)
        {
            return 0;
        }

        var state = await db.Set<GoldpathArchiveChainState>().FindAsync([definition.Name], ct)
            ?? db.Add(new GoldpathArchiveChainState { Definition = definition.Name }).Entity;

        var archivedCount = 0;
        var now = _time.GetUtcNow();
        foreach (var key in fresh)
        {
            var candidate = await definition.LoadAsync(db, key, ct);
            if (candidate is null)
            {
                continue;   // raced away (or key formatting drift) — the next run re-discovers
            }

            var document = JsonSerializer.Serialize(candidate.Root, definition.RootType, GoldpathArchiveEnvelope.Json);
            // Hashed and stored as ONE value, at a precision every store keeps (#383).
            var dueAt = GoldpathArchiveEnvelope.StoredPrecision(candidate.DueAt);
            var contentHash = GoldpathArchiveEnvelope.ContentHash(
                GoldpathArchiveEnvelope.SchemaVersion, definition.Name, key, candidate.Tenant, dueAt, document);
            var index = state.LastIndex + 1;
            var chainHash = GoldpathArchiveEnvelope.ChainHash(contentHash, state.LastHash, index);   // genesis: LastHash is ""

            db.Add(new GoldpathArchiveEntry
            {
                Definition = definition.Name,
                AggregateKey = key,
                Tenant = candidate.Tenant,
                Document = document,
                SchemaVersion = GoldpathArchiveEnvelope.SchemaVersion,
                DueAt = dueAt,
                ArchivedAt = now,
                ChainIndex = index,
                ContentHash = contentHash,
                ChainHash = chainHash,
                PreviousHash = state.LastHash,
            });
            state.LastIndex = index;
            state.LastHash = chainHash;

            if (definition.DeleteHotRows)
            {
                await definition.RemoveHotAsync(db, key, ct);
            }

            archivedCount++;
        }

        await db.SaveChangesAsync(ct);   // entries + chain state + hot removals: one commit
        GoldpathArchivalMetrics.Appended(definition.Name, archivedCount);
        _logger.LogInformation("Archive {Definition}: {Count} aggregates moved (chain at {Index}).",
            definition.Name, archivedCount, state.LastIndex);
        return archivedCount;
    }

    /// <summary>Purges expired archive entries — active legal holds are EXEMPT (D4).</summary>
    public async Task<int> PurgeExpiredEntriesAsync(IServiceProvider services, GoldpathArchiveDefinition definition, int batchSize, CancellationToken ct)
    {
        if (definition.RetainFor is not { } retainFor)
        {
            return 0;
        }

        var db = services.GetRequiredService<TContext>();
        var cutoff = _time.GetUtcNow() - retainFor;

        var expired = await db.Set<GoldpathArchiveEntry>()
            .Where(e => e.Definition == definition.Name && e.ArchivedAt <= cutoff)
            .Where(e => !db.Set<GoldpathLegalHold>().Any(h =>
                h.Definition == e.Definition && h.AggregateKey == e.AggregateKey && h.LiftedAt == null))
            .OrderBy(e => e.ChainIndex)
            .Take(batchSize)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return 0;
        }

        // The chain must stay verifiable: only a contiguous PREFIX may leave. A held entry
        // in the middle stops the purge AT it (the hold protects everything behind it too —
        // that is the honest reading of "exempt").
        var state = await db.Set<GoldpathArchiveChainState>().FindAsync([definition.Name], ct)
            ?? throw new InvalidOperationException($"No chain state for '{definition.Name}' — purge before any archive run?");
        var purgeable = new List<GoldpathArchiveEntry>();
        var expectedIndex = state.PurgedThroughIndex + 1;
        foreach (var entry in expired)
        {
            if (entry.ChainIndex != expectedIndex)
            {
                break;
            }

            purgeable.Add(entry);
            expectedIndex++;
        }

        if (purgeable.Count == 0)
        {
            return 0;
        }

        state.PurgedThroughIndex = purgeable[^1].ChainIndex;
        state.PurgedHeadHash = purgeable[^1].ChainHash;
        db.RemoveRange(purgeable);
        await db.SaveChangesAsync(ct);
        GoldpathArchivalMetrics.Purged(definition.Name, purgeable.Count);
        _logger.LogInformation("Purge {Definition}: {Count} expired entries removed (through {Index}).",
            definition.Name, purgeable.Count, state.PurgedThroughIndex);
        return purgeable.Count;
    }

    /// <summary>
    /// Verifies a slice of the chain: link continuity on <see cref="GoldpathArchiveEntry.ChainHash"/>,
    /// content integrity on <see cref="GoldpathArchiveEntry.ContentHash"/>, and the erasure rule
    /// (a content/chain divergence without <see cref="GoldpathArchiveEntry.ErasedAt"/> is tamper).
    /// </summary>
    public async Task<List<GoldpathChainFinding>> VerifySliceAsync(IServiceProvider services, GoldpathArchiveDefinition definition, long fromIndex, long toIndex, CancellationToken ct)
    {
        var db = services.GetRequiredService<TContext>();
        var findings = new List<GoldpathChainFinding>();
        var state = await db.Set<GoldpathArchiveChainState>().AsNoTracking().FirstOrDefaultAsync(s => s.Definition == definition.Name, ct);
        if (state is null)
        {
            return findings;   // nothing archived yet — nothing to verify
        }

        var entries = await db.Set<GoldpathArchiveEntry>().AsNoTracking()
            .Where(e => e.Definition == definition.Name && e.ChainIndex >= fromIndex && e.ChainIndex <= toIndex)
            .OrderBy(e => e.ChainIndex)
            .ToListAsync(ct);

        var previousHash = fromIndex == state.PurgedThroughIndex + 1
            ? state.PurgedHeadHash
            : await db.Set<GoldpathArchiveEntry>().AsNoTracking()
                .Where(e => e.Definition == definition.Name && e.ChainIndex == fromIndex - 1)
                .Select(e => e.ChainHash)
                .FirstOrDefaultAsync(ct) ?? "";
        var expectedIndex = fromIndex;

        foreach (var entry in entries)
        {
            if (entry.ChainIndex != expectedIndex)
            {
                findings.Add(new GoldpathChainFinding(definition.Name, expectedIndex, "", $"missing entry at index {expectedIndex}"));
                expectedIndex = entry.ChainIndex;
            }

            if (entry.PreviousHash != previousHash)
            {
                findings.Add(new GoldpathChainFinding(definition.Name, entry.ChainIndex, entry.AggregateKey, "chain link broken"));
            }

            var currentContent = GoldpathArchiveEnvelope.ContentHash(
                entry.SchemaVersion, entry.Definition, entry.AggregateKey, entry.Tenant, entry.DueAt, entry.Document);
            if (currentContent != entry.ContentHash)
            {
                findings.Add(new GoldpathChainFinding(definition.Name, entry.ChainIndex, entry.AggregateKey, "document does not match its content hash"));
            }

            // A2: erasure re-stamps ContentHash but keeps the SEALED hash — so the chain
            // check never skips. An erased row with no retained hash is itself a finding.
            var appendContent = entry.ErasedAt is null ? entry.ContentHash : entry.PreErasureContentHash;
            if (appendContent is null)
            {
                findings.Add(new GoldpathChainFinding(definition.Name, entry.ChainIndex, entry.AggregateKey, "erased entry retains no pre-erasure content hash — the chain cannot vouch for it"));
            }
            else if (GoldpathArchiveEnvelope.ChainHash(appendContent, entry.PreviousHash, entry.ChainIndex) != entry.ChainHash)
            {
                findings.Add(new GoldpathChainFinding(definition.Name, entry.ChainIndex, entry.AggregateKey, "content diverged from the sealed chain hash without an erasure record"));
            }

            previousHash = entry.ChainHash;
            expectedIndex++;
        }

        return findings;
    }

    /// <summary>Retrieves one archived aggregate (the finance p95 &lt; 5s budget lives here).</summary>
    public async Task<GoldpathArchiveEntry?> RetrieveAsync(IServiceProvider services, string definition, string aggregateKey, CancellationToken ct)
    {
        var db = services.GetRequiredService<TContext>();
        return await db.Set<GoldpathArchiveEntry>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Definition == definition && e.AggregateKey == aggregateKey, ct);
    }
}
