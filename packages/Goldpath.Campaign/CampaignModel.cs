using Microsoft.EntityFrameworkCore;

namespace Goldpath;

/// <summary>Lifecycle of a campaign instance (campaign RFC D1/D6).</summary>
public enum GoldpathCampaignState
{
    /// <summary>Created; the pacer has not adopted it yet.</summary>
    Created = 0,

    /// <summary>The leader is materializing targets into items (streaming, ceilinged).</summary>
    Enumerating = 1,

    /// <summary>Items release under policy.</summary>
    Running = 2,

    /// <summary>An operator paused it; the pacer skips it, in-flight items drain.</summary>
    Paused = 3,

    /// <summary>Every item terminal, none failed. Terminal.</summary>
    Completed = 4,

    /// <summary>Every item terminal, some failed (repair/replay may still heal them).</summary>
    CompletedWithFailures = 5,

    /// <summary>Operator abort: remaining Pending items were terminal-stamped as Aborted. Terminal.</summary>
    Aborted = 6,

    /// <summary>
    /// The policy's <see cref="GoldpathCampaign.EndDate"/> passed with work remaining
    /// (R1.2): whatever is left is a REPORT, never a background surprise. Terminal.
    /// </summary>
    ExpiredIncomplete = 7,
}

/// <summary>
/// How much of a SHARED ceiling a campaign is entitled to when several campaigns compete
/// for it (campaign RFC R2.5). Only a shared bucket (GlobalTps, a type's MaxTps) reads it;
/// a campaign alone under its own policy releases at its own Tps whatever its priority.
/// Normal is 0 on purpose: every row that predates the column is Normal.
/// </summary>
public enum GoldpathCampaignPriority
{
    /// <summary>The default: weight 2.</summary>
    Normal = 0,

    /// <summary>Weight 3 — an urgent campaign takes one and a half times a normal one's share under contention.</summary>
    High = 1,

    /// <summary>Weight 1 — bulk work that yields to everything else, never to zero.</summary>
    Low = 2,
}

/// <summary>One item's journey (lean ON PURPOSE — this table reaches 30M rows).</summary>
public enum GoldpathCampaignItemState
{
    /// <summary>Materialized, not yet released to the broker.</summary>
    Pending = 0,

    /// <summary>Published to the broker; no consumer claimed it yet.</summary>
    Released = 1,

    /// <summary>A consumer CLAIMED it (state-guarded) — the external call may be in flight.</summary>
    Processing = 2,

    /// <summary>Handler succeeded. Terminal.</summary>
    Succeeded = 3,

    /// <summary>Handler exhausted/failed; sits in the repair story. Terminal until replay.</summary>
    Failed = 4,

    /// <summary>Campaign aborted before this item released. Terminal.</summary>
    Aborted = 5,

    /// <summary>
    /// A transient failure with attempts left (R1.3): waits out its backoff rung, then
    /// the pacer re-releases it. NOT terminal — it still occupies an in-flight slot,
    /// which is honest backpressure.
    /// </summary>
    AwaitingRetry = 6,

    /// <summary>
    /// The target system ACCEPTED the request and will answer through the callback surface
    /// (R2.1). NOT terminal — it occupies an in-flight slot until the callback or the ack
    /// deadline settles it; the deadline turns it into a retryable failure, never a success.
    /// </summary>
    AwaitingAck = 7,
}

/// <summary>A campaign INSTANCE: created at runtime over a code-registered type (D1).</summary>
public class GoldpathCampaign
{
    /// <summary>Surrogate id (the public handle of every verb).</summary>
    public Guid Id { get; set; }

    /// <summary>The registered campaign-type key.</summary>
    public string Type { get; set; } = "";

    /// <summary>Operator-facing display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Lifecycle state — the optimistic-concurrency token.</summary>
    public GoldpathCampaignState State { get; set; }

    /// <summary>Selector parameters as JSON (the type's Targets closure consumes them).</summary>
    public string ParametersJson { get; set; } = "{}";

    // ---- policy (all LIVE-adjustable, D6) ----

    /// <summary>Release ceiling per second.</summary>
    public int Tps { get; set; }

    /// <summary>Releases allowed per calendar day in the policy timezone (null = unlimited).</summary>
    public int? DailyQuota { get; set; }

    /// <summary>Released-but-not-terminal ceiling (broker/consumer protection).</summary>
    public int MaxInFlight { get; set; }

    /// <summary>Window start (local to <see cref="TimeZoneId"/>; null = always open).</summary>
    public TimeOnly? WindowStart { get; set; }

    /// <summary>Window end.</summary>
    public TimeOnly? WindowEnd { get; set; }

    /// <summary>IANA/Windows timezone the window and quota-day are evaluated in.</summary>
    public string TimeZoneId { get; set; } = "UTC";

    /// <summary>Days (policy timezone) on which nothing releases (R1.1); CSV of DayOfWeek names, null = none.</summary>
    public string? ExcludedDays { get; set; }

    /// <summary>Last calendar day (policy timezone) the campaign may release on (R1.2); null = open-ended.</summary>
    public DateOnly? EndDate { get; set; }

    /// <summary>Attempts per item before it lands in the repair queue (R1.3); 1 = no auto-retry.</summary>
    public int MaxAttempts { get; set; } = 1;

    /// <summary>Share of a contended shared ceiling (R2.5); live-adjustable like the rest of the policy.</summary>
    public GoldpathCampaignPriority Priority { get; set; }

    // ---- watermarks & durable truth (D3) ----

    /// <summary>Items materialized so far (enumeration watermark = the next Seq).</summary>
    public long EnumeratedThrough { get; set; }

    /// <summary>True when the selector stream is exhausted (TotalItems is final).</summary>
    public bool EnumerationComplete { get; set; }

    /// <summary>
    /// The key of the last materialized target when the type resumes by KEY (R2.7); null
    /// for count-resumed types. A takeover reopens the selector AFTER this key instead of
    /// re-reading <see cref="EnumeratedThrough"/> rows to skip them.
    /// </summary>
    public string? EnumeratedKey { get; set; }

    /// <summary>Items released to the broker so far (release watermark = the next Seq).</summary>
    public long ReleasedThrough { get; set; }

    /// <summary>Terminal successes (sink-maintained, batched).</summary>
    public long SucceededCount { get; set; }

    /// <summary>Terminal failures (sink-maintained, batched).</summary>
    public long FailedCount { get; set; }

    /// <summary>Releases performed on <see cref="QuotaDay"/> (quota accounting survives takeover).</summary>
    public long ReleasedToday { get; set; }

    /// <summary>The policy-timezone calendar day <see cref="ReleasedToday"/> counts for.</summary>
    public DateOnly QuotaDay { get; set; }

    // ---- evidence ----

    /// <summary>Creation timestamp + actor.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who created it (the admin verb's principal).</summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>Completion/abort timestamp.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Last state-verb evidence (pause/resume/abort/throttle: who + what).</summary>
    public string? LastVerb { get; set; }

    /// <summary>Owning tenant, when tenant-bound.</summary>
    public string? Tenant { get; set; }
}

/// <summary>One target's row — LEAN: this table is the 30M-row one.</summary>
public class GoldpathCampaignItem
{
    /// <summary>Owning campaign.</summary>
    public Guid CampaignId { get; set; }

    /// <summary>Dense 1-based sequence (enumeration order) — the watermark coordinate.</summary>
    public long Seq { get; set; }

    /// <summary>The target as JSON (the handler deserializes the type's TTarget).</summary>
    public string TargetJson { get; set; } = "";

    /// <summary>Item state (guarded updates; no concurrency token at this scale).</summary>
    public GoldpathCampaignItemState State { get; set; }

    /// <summary>
    /// When the leader last published this item (R2.6). A Released item still unclaimed
    /// after <see cref="GoldpathCampaignOptions.OrphanReleaseAfter"/> is an orphan — its
    /// message was dropped (a pause, a broker purge, a consumer that refused the claim) —
    /// and the pacer publishes it again. Null = due now (a resume stamps it so).
    /// </summary>
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Stamped by the CONSUMER's claim, before any external call (constraint 2).</summary>
    public DateTimeOffset? ClaimedAt { get; set; }

    /// <summary>Terminal timestamp (sink-batched).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Failure detail (bounded; the repair story's teaching text).</summary>
    public string? Error { get; set; }

    /// <summary>Delivery attempts consumed (R1.3) — the ladder's bookkeeping, one small column.</summary>
    public int Attempts { get; set; }

    /// <summary>When an <see cref="GoldpathCampaignItemState.AwaitingRetry"/> item may be re-released (R2.3; jittered).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>When an <see cref="GoldpathCampaignItemState.AwaitingAck"/> item stops waiting for its callback (R2.1).</summary>
    public DateTimeOffset? AckDeadline { get; set; }

    /// <summary>The id the target system's callback names (R2.1; acceptances only).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>The provider's short, stable error code (R2.1; reports group by it).</summary>
    public string? ErrorCode { get; set; }
}

/// <summary>
/// One admin action — the jobs console's iron rule, kept: no admin action without an
/// audit record. Written by the admin service for EVERY mutating verb.
/// </summary>
public class GoldpathCampaignAudit
{
    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>When the verb executed (UTC).</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Who invoked it (auth principal name; "anonymous" only outside the auth floor).</summary>
    public string Actor { get; set; } = "";

    /// <summary>The verb (create, pause, resume, abort, throttle).</summary>
    public string Action { get; set; } = "";

    /// <summary>The campaign the verb targeted.</summary>
    public Guid CampaignId { get; set; }

    /// <summary>Verb-specific detail (policy old→new, abort reason...).</summary>
    public string? Detail { get; set; }
}

/// <summary>Maps the campaign tables onto the app's own DbContext (same database).</summary>
public static class GoldpathCampaignModel
{
    /// <summary>Adds campaigns + items to the model.</summary>
    public static ModelBuilder AddGoldpathCampaign(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoldpathCampaign>(campaign =>
        {
            campaign.ToTable("GoldpathCampaigns");
            campaign.HasKey(c => c.Id);
            campaign.Property(c => c.Id).ValueGeneratedNever();
            campaign.Property(c => c.Type).HasMaxLength(128);
            campaign.Property(c => c.Name).HasMaxLength(256);
            campaign.Property(c => c.TimeZoneId).HasMaxLength(64);
            campaign.Property(c => c.ExcludedDays).HasMaxLength(64);
            campaign.Property(c => c.CreatedBy).HasMaxLength(256);
            campaign.Property(c => c.LastVerb).HasMaxLength(512);
            campaign.Property(c => c.Tenant).HasMaxLength(128);
            campaign.Property(c => c.EnumeratedKey).HasMaxLength(256);
            campaign.Property(c => c.ParametersJson).HasMaxLength(-1);   // caller's selector parameters — a DOCUMENT (#198)
            campaign.Property(c => c.State).IsConcurrencyToken();
            campaign.HasIndex(c => new { c.State, c.CreatedAt });
        });

        modelBuilder.Entity<GoldpathCampaignItem>(item =>
        {
            item.ToTable("GoldpathCampaignItems");
            item.HasKey(i => new { i.CampaignId, i.Seq });
            item.Property(i => i.TargetJson).HasMaxLength(-1);   // the serialized target — a DOCUMENT (#198); truncation corrupts every wide row of the 30M-row table
            item.Property(i => i.Error).HasMaxLength(1024);
            item.Property(i => i.ErrorCode).HasMaxLength(64);
            item.Property(i => i.CorrelationId).HasMaxLength(128);
            // R2: the ripeness query (State + NextAttemptAt) and the callback lookup (CorrelationId)
            // each get the index they walk; the old (CampaignId, State) prefix survives inside the first.
            item.HasIndex(i => new { i.CampaignId, i.State, i.NextAttemptAt });
            item.HasIndex(i => new { i.CampaignId, i.CorrelationId });
            // R2.6: the orphan sweep walks Released items by age.
            item.HasIndex(i => new { i.CampaignId, i.State, i.ReleasedAt });
        });

        modelBuilder.Entity<GoldpathCampaignAudit>(audit =>
        {
            audit.ToTable("GoldpathCampaignAudit");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Actor).HasMaxLength(256);
            audit.Property(a => a.Action).HasMaxLength(64);
            audit.Property(a => a.Detail).HasMaxLength(1024);
            audit.HasIndex(a => new { a.CampaignId, a.At });
        });

        return modelBuilder;
    }
}
