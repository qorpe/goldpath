using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>A processed-row mark: the durable half of the (rail, file, line) idempotency key.</summary>
public sealed class GoldpathFileProcessedRow
{
    /// <summary>Rail name.</summary>
    public string Rail { get; set; } = "";

    /// <summary>File name.</summary>
    public string File { get; set; } = "";

    /// <summary>1-based line number.</summary>
    public int Line { get; set; }

    /// <summary>
    /// SHA-256 hex (64 characters) of the line's content when it was processed (#482); null on a row marked before the
    /// column existed, which the engine counts as matching.
    /// </summary>
    public string? ContentHash { get; set; }
}

/// <summary>A quarantined row with its reason.</summary>
public sealed class GoldpathFileQuarantineRow
{
    /// <summary>Rail name.</summary>
    public string Rail { get; set; } = "";

    /// <summary>File name.</summary>
    public string File { get; set; } = "";

    /// <summary>1-based line number.</summary>
    public int Line { get; set; }

    /// <summary>Why the row quarantined.</summary>
    public string Reason { get; set; } = "";

    /// <summary>When the row FIRST quarantined (an upsert on reprocess keeps it) — the age the runbook watches.</summary>
    public DateTimeOffset QuarantinedAt { get; set; }
}

/// <summary>An archive mark for a completed file run.</summary>
public sealed class GoldpathFileArchiveRow
{
    /// <summary>When the file's run completed and the archive mark was written.</summary>
    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>Rail name.</summary>
    public string Rail { get; set; } = "";

    /// <summary>File name.</summary>
    public string File { get; set; } = "";
}

/// <summary>Model mapping for the database-backed file ledger.</summary>
public static class GoldpathFileExchangeModelExtensions
{
    /// <summary>Maps the ledger tables. Call from the app context's <c>OnModelCreating</c>.</summary>
    public static ModelBuilder AddGoldpathFileExchangeModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldpathFileProcessedRow>(e =>
        {
            e.ToTable("GoldpathFileProcessed");
            e.HasKey(x => new { x.Rail, x.File, x.Line });
            e.Property(x => x.Rail).HasMaxLength(128);
            e.Property(x => x.File).HasMaxLength(256);
            e.Property(x => x.ContentHash).HasMaxLength(64);   // nullable: rows marked before #482 carry none
        });
        modelBuilder.Entity<GoldpathFileQuarantineRow>(e =>
        {
            e.ToTable("GoldpathFileQuarantine");
            e.HasKey(x => new { x.Rail, x.File, x.Line });
            e.Property(x => x.Rail).HasMaxLength(128);
            e.Property(x => x.File).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(1024);
        });
        modelBuilder.Entity<GoldpathFileArchiveRow>(e =>
        {
            e.ToTable("GoldpathFileArchive");
            e.HasKey(x => new { x.Rail, x.File });
            e.Property(x => x.Rail).HasMaxLength(128);
            e.Property(x => x.File).HasMaxLength(256);
        });
        return modelBuilder;
    }
}

/// <summary>
/// The database-backed ledger: rail progress lives in the app's own DbContext (mapped by
/// <see cref="GoldpathFileExchangeModelExtensions.AddGoldpathFileExchangeModel"/>), so the
/// zero-duplicate guarantee survives restarts and holds across nodes. Each call runs in its
/// own scope — the engine stays a singleton.
/// </summary>
public sealed class GoldpathEfFileLedger<TContext> : IGoldpathFileLedger, IGoldpathFileLedgerQueries
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    /// <summary>Registered by <c>AddGoldpathFileExchange&lt;TBuilder, TContext&gt;</c>.</summary>
    public GoldpathEfFileLedger(IServiceScopeFactory scopes) : this(scopes, TimeProvider.System)
    {
    }

    /// <summary>The ledger on <paramref name="clock"/> (tests pin the quarantine age).</summary>
    public GoldpathEfFileLedger(IServiceScopeFactory scopes, TimeProvider clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<bool> IsProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathFileProcessedRow>().AsNoTracking()
            .AnyAsync(x => x.Rail == rail && x.File == file && x.Line == line, cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkProcessedAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(new GoldpathFileProcessedRow { Rail = rail, File = file, Line = line });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetProcessedHashAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var row = await db.Set<GoldpathFileProcessedRow>().AsNoTracking()
            .Where(x => x.Rail == rail && x.File == file && x.Line == line)
            .Select(x => new { x.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : row.ContentHash ?? "";
    }

    /// <inheritdoc />
    public async Task MarkProcessedWithHashAsync(string rail, string file, int line, string contentHash, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        db.Add(new GoldpathFileProcessedRow { Rail = rail, File = file, Line = line, ContentHash = contentHash });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task QuarantineAsync(string rail, string file, int line, string reason, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var existing = await db.Set<GoldpathFileQuarantineRow>()
            .SingleOrDefaultAsync(x => x.Rail == rail && x.File == file && x.Line == line, cancellationToken);
        if (existing is null)
        {
            db.Add(new GoldpathFileQuarantineRow { Rail = rail, File = file, Line = line, Reason = reason, QuarantinedAt = _clock.GetUtcNow() });
        }
        else
        {
            // The FIRST quarantine time survives the upsert: the age an operator triages is
            // how long the row has waited, not how recently the same failure repeated.
            existing.Reason = reason;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseQuarantineAsync(string rail, string file, int line, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var existing = await db.Set<GoldpathFileQuarantineRow>()
            .SingleOrDefaultAsync(x => x.Rail == rail && x.File == file && x.Line == line, cancellationToken);
        if (existing is not null)
        {
            db.Remove(existing);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathQuarantinedRow>> GetQuarantineAsync(string rail, string file, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var rows = await db.Set<GoldpathFileQuarantineRow>().AsNoTracking()
            .Where(x => x.Rail == rail && x.File == file)
            .OrderBy(x => x.Line)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new GoldpathQuarantinedRow(r.Rail, r.File, r.Line, r.Reason)).ToList();
    }

    /// <inheritdoc />
    public async Task MarkArchivedAsync(string rail, string file, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var exists = await db.Set<GoldpathFileArchiveRow>().AsNoTracking()
            .AnyAsync(x => x.Rail == rail && x.File == file, cancellationToken);
        if (!exists)
        {
            db.Add(new GoldpathFileArchiveRow { Rail = rail, File = file, ArchivedAt = _clock.GetUtcNow() });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    // The admin reads project LIGHT rows and sort on the client: SQLite cannot ORDER BY a
    // DateTimeOffset column (the approvals store learned it first), and the surface is a
    // take-bounded triage view, not a reporting query. The read window is AdminPaging.MaxTake
    // per table — the same bound the contract puts on every list.

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathFileInfo>> ListFilesAsync(string? rail, int take, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var archives = await db.Set<GoldpathFileArchiveRow>().AsNoTracking()
            .Where(x => rail == null || x.Rail == rail)
            .Select(x => new { x.Rail, x.File, x.ArchivedAt })
            .ToListAsync(cancellationToken);
        var quarantine = await db.Set<GoldpathFileQuarantineRow>().AsNoTracking()
            .Where(x => rail == null || x.Rail == rail)
            .GroupBy(x => new { x.Rail, x.File })
            .Select(g => new { g.Key.Rail, g.Key.File, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var processed = await db.Set<GoldpathFileProcessedRow>().AsNoTracking()
            .Where(x => rail == null || x.Rail == rail)
            .GroupBy(x => new { x.Rail, x.File })
            .Select(g => new { g.Key.Rail, g.Key.File, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var keys = archives.Select(a => (a.Rail, a.File))
            .Concat(quarantine.Select(q => (q.Rail, q.File)))
            .Concat(processed.Select(p => (p.Rail, p.File)))
            .Distinct();
        return keys
            .Select(k =>
            {
                var archive = archives.FirstOrDefault(a => a.Rail == k.Rail && a.File == k.File);
                return new GoldpathFileInfo(
                    k.Rail,
                    k.File,
                    processed.FirstOrDefault(p => p.Rail == k.Rail && p.File == k.File)?.Count ?? 0,
                    quarantine.FirstOrDefault(q => q.Rail == k.Rail && q.File == k.File)?.Count ?? 0,
                    archive is not null,
                    archive?.ArchivedAt);
            })
            .OrderByDescending(f => f.ArchivedAt ?? DateTimeOffset.MaxValue)   // in-flight (unarchived) files first, then newest archive
            .ThenBy(f => f.Rail, StringComparer.Ordinal).ThenBy(f => f.File, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GoldpathFileQuarantineInfo>> ListQuarantineAsync(string? rail, int take, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var rows = await db.Set<GoldpathFileQuarantineRow>().AsNoTracking()
            .Where(x => rail == null || x.Rail == rail)
            .Select(x => new { x.Rail, x.File, x.Line, x.Reason, x.QuarantinedAt })
            .ToListAsync(cancellationToken);
        return rows
            .OrderBy(x => x.QuarantinedAt).ThenBy(x => x.Rail, StringComparer.Ordinal).ThenBy(x => x.File, StringComparer.Ordinal).ThenBy(x => x.Line)
            .Take(take)
            .Select(x => new GoldpathFileQuarantineInfo(x.Rail, x.File, x.Line, x.Reason, x.QuarantinedAt))
            .ToList();
    }
}
