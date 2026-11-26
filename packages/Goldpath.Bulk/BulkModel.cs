using Microsoft.EntityFrameworkCore;

namespace Goldpath;

/// <summary>Lifecycle of a bulk batch (bulk RFC D2). Transitions are engine-guarded.</summary>
public enum GoldpathBulkBatchState
{
    /// <summary>File stored; validation not started.</summary>
    Received = 0,

    /// <summary>The validate run is parsing and validating rows.</summary>
    Validating = 1,

    /// <summary>Report ready; waiting at the approval gate (or auto-approved past it).</summary>
    Validated = 2,

    /// <summary>Gate passed; the execute run will pick the batch up.</summary>
    Approved = 3,

    /// <summary>Gate refused (or the engine refused: row ceiling). Terminal.</summary>
    Rejected = 4,

    /// <summary>Rows are executing as a jobs run.</summary>
    Executing = 5,

    /// <summary>Every valid row executed. Terminal.</summary>
    Completed = 6,

    /// <summary>Execution finished with rows in the repair queue; replay can still flip this to Completed.</summary>
    CompletedWithFailures = 7,
}

/// <summary>An uploaded file: content-addressed (SHA-256), stored as chunked blob rows (D1).</summary>
public class GoldpathBulkFile
{
    /// <summary>Surrogate id.</summary>
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the bytes, lowercase hex — the dedup identity.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Client file name (display only; never trusted as identity).</summary>
    public string FileName { get; set; } = "";

    /// <summary>Total length in bytes.</summary>
    public long Length { get; set; }

    /// <summary>Upload timestamp (UTC).</summary>
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>One chunk of an uploaded file's bytes (streamed writes, streamed reads).</summary>
public class GoldpathBulkFileChunk
{
    /// <summary>Owning file.</summary>
    public Guid FileId { get; set; }

    /// <summary>Zero-based chunk position.</summary>
    public int Index { get; set; }

    /// <summary>The bytes (bounded by <see cref="GoldpathBulkFileStore{TContext}.ChunkBytes"/>).</summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>A batch: one file bound to one definition, walking the D2 state machine.</summary>
public class GoldpathBulkBatch
{
    /// <summary>Surrogate id (the public handle of every verb).</summary>
    public Guid Id { get; set; }

    /// <summary>The batch definition name (registration key).</summary>
    public string Definition { get; set; } = "";

    /// <summary>The uploaded file.</summary>
    public Guid FileId { get; set; }

    /// <summary>Current lifecycle state — the optimistic-concurrency token: transitions never race silently.</summary>
    public GoldpathBulkBatchState State { get; set; }

    /// <summary>Owning tenant (fail-closed scoping), when the app is tenant-bound.</summary>
    public string? Tenant { get; set; }

    /// <summary>Parsed data rows (header excluded).</summary>
    public int TotalRows { get; set; }

    /// <summary>Rows that passed validation.</summary>
    public int ValidRows { get; set; }

    /// <summary>Rows with at least one validation error.</summary>
    public int InvalidRows { get; set; }

    /// <summary>Valid rows executed successfully so far.</summary>
    public int ExecutedRows { get; set; }

    /// <summary>Valid rows currently failed into the repair queue.</summary>
    public int FailedRows { get; set; }

    /// <summary>The jobs run executing (or having executed) this batch.</summary>
    public Guid? RunId { get; set; }

    /// <summary>Upload timestamp.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>
    /// W3C traceparent of the request that uploaded the file — every later span over this
    /// batch (validate, execute, replay) links back to it, so ONE trace id follows an
    /// instruction from the HTTP entry to its downstream effect (H4).
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>Validation-report timestamp.</summary>
    public DateTimeOffset? ValidatedAt { get; set; }

    /// <summary>Gate decision timestamp (approve or reject).</summary>
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>Who decided at the gate ("goldpath" when the engine refused: row ceiling).</summary>
    public string? DecidedBy { get; set; }

    /// <summary>Free-form decision note (rejection reason, ticket reference).</summary>
    public string? DecisionNote { get; set; }

    /// <summary>Execution-finished timestamp.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// One VALID parsed row: the typed payload (JSON) plus its execution stamps. Invalid rows
/// store no payload — their story is told by <see cref="GoldpathBulkRowError"/> (privacy by
/// construction, D6).
/// </summary>
public class GoldpathBulkRow
{
    /// <summary>Owning batch.</summary>
    public Guid BatchId { get; set; }

    /// <summary>1-based data row number (header excluded) — the report/repair coordinate.</summary>
    public int RowNumber { get; set; }

    /// <summary>The parsed row as JSON (deserialized back for the handler).</summary>
    public string Payload { get; set; } = "";

    /// <summary>
    /// Set when the executing chunk CLAIMS the row, BEFORE any side effect (MDM constraint 2):
    /// a claimed-but-unstamped row after a crash goes to the repair queue instead of being
    /// silently re-sent.
    /// </summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Set when the handler completed the row.</summary>
    public DateTimeOffset? ExecutedAt { get; set; }

    /// <summary>Set when the handler failed the row into the repair queue (replay clears it).</summary>
    public DateTimeOffset? FailedAt { get; set; }
}

/// <summary>
/// One validation finding: row + field + message, NEVER the offending value (D6) — the raw
/// file is the evidence for those with access; reports stay classified-data-free.
/// </summary>
public class GoldpathBulkRowError
{
    /// <summary>Surrogate id.</summary>
    public long Id { get; set; }

    /// <summary>Owning batch.</summary>
    public Guid BatchId { get; set; }

    /// <summary>1-based data row number; 0 for file-level findings.</summary>
    public int RowNumber { get; set; }

    /// <summary>The failing field/property, or "(line)" / "(file)" for structural findings.</summary>
    public string Field { get; set; } = "";

    /// <summary>Teaching message — value-free by contract.</summary>
    public string Message { get; set; } = "";
}

/// <summary>Maps the bulk intake tables onto the app's own DbContext (same database, D1).</summary>
public static class GoldpathBulkModel
{
    /// <summary>Adds files, batches, rows and row errors to the model.</summary>
    public static ModelBuilder AddGoldpathBulk(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldpathBulkFile>(file =>
        {
            file.ToTable("GoldpathBulkFiles");
            file.HasKey(f => f.Id);
            file.Property(f => f.Id).ValueGeneratedNever();
            file.Property(f => f.Sha256).HasMaxLength(64);
            file.Property(f => f.FileName).HasMaxLength(260);
            file.HasIndex(f => f.Sha256).IsUnique();
        });

        modelBuilder.Entity<GoldpathBulkFileChunk>(chunk =>
        {
            chunk.ToTable("GoldpathBulkFileChunks");
            chunk.HasKey(c => new { c.FileId, c.Index });
            if (IsOracle(modelBuilder))
            {
                // #559: a chunk is up to 256 KiB (GoldpathBulkFileStore.ChunkBytes), and the Oracle provider maps an unbounded
                // byte[] to RAW(2000), which refuses any chunk above 2,000 bytes (ORA-01460). PostgreSQL's bytea and SQL
                // Server's varbinary(max) already hold a chunk, so only Oracle is shaped.
                chunk.Property(c => c.Data).HasColumnType("BLOB");
            }
        });

        modelBuilder.Entity<GoldpathBulkBatch>(batch =>
        {
            batch.ToTable("GoldpathBulkBatches");
            batch.HasKey(b => b.Id);
            batch.Property(b => b.Id).ValueGeneratedNever();
            batch.Property(b => b.Definition).HasMaxLength(128);
            batch.Property(b => b.TraceParent).HasMaxLength(55);   // 00-<32>-<16>-00, W3C fixed shape
            batch.Property(b => b.Tenant).HasMaxLength(128);
            batch.Property(b => b.DecidedBy).HasMaxLength(256);
            batch.Property(b => b.DecisionNote).HasMaxLength(1024);
            batch.Property(b => b.State).IsConcurrencyToken();
            batch.HasIndex(b => new { b.Definition, b.State });
            batch.HasIndex(b => b.FileId);
        });

        modelBuilder.Entity<GoldpathBulkRow>(row =>
        {
            row.ToTable("GoldpathBulkRows");
            row.HasKey(r => new { r.BatchId, r.RowNumber });
            // The parsed row as JSON — a DOCUMENT (#198): a conventions host defaults
            // strings to 256, and a truncated row fails to deserialize at execute time.
            row.Property(r => r.Payload).HasMaxLength(-1);
        });

        modelBuilder.Entity<GoldpathBulkRowError>(error =>
        {
            error.ToTable("GoldpathBulkRowErrors");
            error.HasKey(e => e.Id);
            error.Property(e => e.Field).HasMaxLength(128);
            error.Property(e => e.Message).HasMaxLength(512);
            error.HasIndex(e => new { e.BatchId, e.RowNumber });
        });

        return modelBuilder;
    }

    /// <summary>
    /// The Oracle EF provider marks the model it builds with its own annotations (<c>Oracle:…</c>) before
    /// <c>OnModelCreating</c> runs; nothing else does, so the PostgreSQL, SQL Server and SQLite models never take this path
    /// (the test the Jobs and Archival models use too).
    /// </summary>
    internal static bool IsOracle(ModelBuilder modelBuilder)
        => modelBuilder.Model.GetAnnotations().Any(a => a.Name.StartsWith("Oracle:", StringComparison.Ordinal));
}
