using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Goldpath;

/// <summary>
/// One archived aggregate: the serialized graph plus its place in the tamper-evident chain.
/// Entries are IMMUTABLE by contract — erasure redacts fields inside the document (D4),
/// verification recomputes hashes (D1); nothing ever updates an entry in place except the
/// audited erasure path, which re-stamps the content hash and records itself.
/// </summary>
public class GoldpathArchiveEntry
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The archive definition this entry belongs to.</summary>
    public string Definition { get; set; } = "";

    /// <summary>The aggregate's key (stringified — stable across key types).</summary>
    public string AggregateKey { get; set; } = "";

    /// <summary>Owning tenant, or null in single-tenant deployments.</summary>
    public string? Tenant { get; set; }

    /// <summary>The serialized graph (versioned JSON envelope payload).</summary>
    public string Document { get; set; } = "";

    /// <summary>Envelope schema version (never guess forward).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>When the aggregate became due (its lifecycle event time).</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>When the archive run captured it.</summary>
    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>This entry's position in the per-definition chain (dense, ascending).</summary>
    public long ChainIndex { get; set; }

    /// <summary>
    /// SHA-256 of the CURRENT canonical envelope. Re-stamped by the audited erasure path —
    /// diverging from <see cref="ChainHash"/> WITHOUT <see cref="ErasedAt"/> is tamper.
    /// </summary>
    public string ContentHash { get; set; } = "";

    /// <summary>
    /// SHA-256 sealed at APPEND time (content-hash-at-append + previous chain hash + index).
    /// Never changes — the chain links through this, so erasure redaction cannot break it.
    /// </summary>
    public string ChainHash { get; set; } = "";

    /// <summary>
    /// The previous entry's chain hash ("" for the genesis entry). On Oracle, which stores <c>''</c> as NULL, the
    /// genesis link is held as NULL and a materialised entry still reads it as <c>""</c> here (#547); a query that
    /// PROJECTS this column reads NULL for it there.
    /// </summary>
    public string PreviousHash
    {
        get => _previousHash ?? "";
        set => _previousHash = value;
    }

    // EF maps this field (its convention finds it by name); null only where Oracle read the genesis link back as NULL.
    private string? _previousHash = "";

    /// <summary>Set when an erasure redacted classified fields (the evidence pointer).</summary>
    public DateTimeOffset? ErasedAt { get; set; }

    /// <summary>
    /// The content hash as it was SEALED into the chain, kept verbatim when erasure
    /// re-stamps <see cref="ContentHash"/> — the chain stays verifiable after redaction
    /// (audit finding A2: without this, an attacker who can write the table erases their
    /// tamper by re-stamping the hash and flagging the row erased).
    /// </summary>
    public string? PreErasureContentHash { get; set; }
}

/// <summary>Per-definition chain head + purged-prefix bookkeeping (append is single-writer via Jobs).</summary>
public class GoldpathArchiveChainState
{
    /// <summary>The archive definition.</summary>
    public string Definition { get; set; } = "";

    /// <summary>Index of the newest entry.</summary>
    public long LastIndex { get; set; }

    /// <summary>
    /// Hash of the newest entry (the append anchor); "" — the genesis anchor — until the first entry is appended. On
    /// Oracle that "" is held as NULL and read back as <c>""</c> into a materialised state (#547).
    /// </summary>
    public string LastHash
    {
        get => _lastHash ?? "";
        set => _lastHash = value;
    }

    // EF maps this field; null only where Oracle read the genesis anchor back as NULL.
    private string? _lastHash = "";

    /// <summary>Retention purges remove the OLDEST prefix; verification starts after it.</summary>
    public long PurgedThroughIndex { get; set; }

    /// <summary>
    /// Hash of the last purged entry — the first kept entry must link to it; "" (the genesis anchor) while nothing
    /// has been purged. On Oracle that "" is held as NULL and read back as <c>""</c> into a materialised state (#547).
    /// </summary>
    public string PurgedHeadHash
    {
        get => _purgedHeadHash ?? "";
        set => _purgedHeadHash = value;
    }

    // EF maps this field; null only where Oracle read the genesis anchor back as NULL.
    private string? _purgedHeadHash = "";
}

/// <summary>An active or lifted legal hold — exempts purge AND hard erasure (D4).</summary>
public class GoldpathLegalHold
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The archive definition the hold scopes to.</summary>
    public string Definition { get; set; } = "";

    /// <summary>The held aggregate key.</summary>
    public string AggregateKey { get; set; } = "";

    /// <summary>The litigation/case reference that justifies the hold.</summary>
    public string CaseReference { get; set; } = "";

    /// <summary>Who placed it.</summary>
    public string PlacedBy { get; set; } = "";

    /// <summary>When it was placed.</summary>
    public DateTimeOffset PlacedAt { get; set; }

    /// <summary>Who lifted it (null while active).</summary>
    public string? LiftedBy { get; set; }

    /// <summary>When it was lifted (null while active).</summary>
    public DateTimeOffset? LiftedAt { get; set; }
}

/// <summary>The KVKK/GDPR evidence row: one erasure request and what it touched.</summary>
public class GoldpathErasureRecord
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The data subject's key (as the requester identified it).</summary>
    public string SubjectKey { get; set; } = "";

    /// <summary>Who executed the request.</summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>When it executed.</summary>
    public DateTimeOffset RequestedAt { get; set; }

    /// <summary>How many archive entries were redacted.</summary>
    public int EntriesAffected { get; set; }

    /// <summary>Free-form context (ticket id, scope notes).</summary>
    public string? Detail { get; set; }
}

/// <summary>Model wiring for the archival tables.</summary>
public static class GoldpathArchiveModelExtensions
{
    /// <summary>
    /// Maps the archive store (entries + chain state), legal holds and erasure evidence —
    /// one call in <c>OnModelCreating</c>, everything rides normal migrations.
    /// </summary>
    public static ModelBuilder AddGoldpathArchiveModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldpathArchiveEntry>(entity =>
        {
            entity.ToTable("GoldpathArchiveEntries");
            // The serialized GRAPH — a DOCUMENT (#198). A conventions host defaults
            // strings to 256; a capped Document silently truncates every archived
            // aggregate AND breaks the hash chain it is supposed to prove.
            entity.Property(e => e.Document).HasMaxLength(-1);
            entity.Property(e => e.Definition).HasMaxLength(120);
            entity.Property(e => e.AggregateKey).HasMaxLength(256);
            entity.Property(e => e.Tenant).HasMaxLength(64);
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.ChainHash).HasMaxLength(64);
            entity.Property(e => e.PreviousHash).HasMaxLength(64);
            entity.Property(e => e.PreErasureContentHash).HasMaxLength(64);
            if (IsOracle(modelBuilder))
            {
                // #547 (owner decision A, the #502 pattern; oracle RFC §2.2): the genesis entry links to the empty
                // hash, and Oracle stores '' as NULL, so a NOT NULL column refuses it (ORA-01400). On Oracle alone the
                // column is nullable, "" is written as NULL and NULL is read back as "": every hash, every chain and
                // every other provider's column stay as they are.
                entity.Property(e => e.PreviousHash).IsRequired(false).HasConversion(GenesisAsNull);
            }

            // The retrieval budget (finance: p95 < 5s at 1M entries) rides this index.
            entity.HasIndex(e => new { e.Definition, e.AggregateKey }).IsUnique();
            entity.HasIndex(e => new { e.Definition, e.ChainIndex }).IsUnique();
            entity.HasIndex(e => new { e.Definition, e.ArchivedAt });
        });

        modelBuilder.Entity<GoldpathArchiveChainState>(entity =>
        {
            entity.ToTable("GoldpathArchiveChainState");
            entity.HasKey(e => e.Definition);
            entity.Property(e => e.Definition).HasMaxLength(120);
            entity.Property(e => e.LastHash).HasMaxLength(64);
            entity.Property(e => e.PurgedHeadHash).HasMaxLength(64);
            if (IsOracle(modelBuilder))
            {
                // #547: both anchors start as the empty genesis hash. PurgedHeadHash stays "" until the first purge;
                // LastHash is "" in a state row saved before any entry was appended (every candidate of the first
                // batch raced away). On Oracle alone both are nullable, written and read as PreviousHash is.
                entity.Property(e => e.LastHash).IsRequired(false).HasConversion(GenesisAsNull);
                entity.Property(e => e.PurgedHeadHash).IsRequired(false).HasConversion(GenesisAsNull);
            }
        });

        modelBuilder.Entity<GoldpathLegalHold>(entity =>
        {
            entity.ToTable("GoldpathLegalHolds");
            entity.Property(e => e.Definition).HasMaxLength(120);
            entity.Property(e => e.AggregateKey).HasMaxLength(256);
            entity.Property(e => e.CaseReference).HasMaxLength(256);
            entity.Property(e => e.PlacedBy).HasMaxLength(256);
            entity.Property(e => e.LiftedBy).HasMaxLength(256);
            entity.HasIndex(e => new { e.Definition, e.AggregateKey, e.LiftedAt });
        });

        modelBuilder.Entity<GoldpathErasureRecord>(entity =>
        {
            entity.ToTable("GoldpathErasureRecords");
            entity.Property(e => e.SubjectKey).HasMaxLength(256);
            entity.Property(e => e.RequestedBy).HasMaxLength(256);
            entity.Property(e => e.Detail).HasMaxLength(1024);   // erasure evidence — truncated compliance context is not evidence (#198)
            entity.HasIndex(e => e.SubjectKey);
        });

        return modelBuilder;
    }

    /// <summary>
    /// The empty genesis hash, written as NULL (Oracle only, #547). A NULL read back lands in the entity's backing
    /// field, and the property reads it as the empty hash, so every chain computes and verifies as it does elsewhere.
    /// </summary>
    private static readonly ValueConverter<string, string> GenesisAsNull = new(
        hash => hash.Length == 0 ? null! : hash,
        stored => stored);

    /// <summary>
    /// The Oracle EF provider marks the model it builds with its own annotations (<c>Oracle:…</c>) before
    /// <c>OnModelCreating</c> runs; nothing else does. The same test as the Quartz store model's in Goldpath.Jobs.
    /// </summary>
    private static bool IsOracle(ModelBuilder modelBuilder)
        => modelBuilder.Model.GetAnnotations().Any(a => a.Name.StartsWith("Oracle:", StringComparison.Ordinal));
}
