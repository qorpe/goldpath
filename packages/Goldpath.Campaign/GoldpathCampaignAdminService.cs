using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>One campaign's console view: durable truth + the math an operator acts on.</summary>
public sealed record GoldpathCampaignInfo(
    Guid Id,
    string Type,
    string Name,
    string State,
    long EnumeratedThrough,
    bool EnumerationComplete,
    long ReleasedThrough,
    long SucceededCount,
    long FailedCount,
    long InFlight,
    long Remaining,
    int Tps,
    int? DailyQuota,
    long ReleasedToday,
    int MaxInFlight,
    TimeOnly? WindowStart,
    TimeOnly? WindowEnd,
    string TimeZoneId,
    IReadOnlyList<string> ExcludedDays,
    DateOnly? EndDate,
    int MaxAttempts,
    GoldpathCampaignPriority Priority,
    bool WindowOpenNow,
    double? EtaSecondsAtCurrentTps,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? CompletedAt,
    string? LastVerb,
    string? Tenant)
{
    /// <summary>
    /// The earliest instant the campaign may start releasing, in UTC (R3.1); null = as soon as its
    /// snapshot is ready. The console shows it in <see cref="TimeZoneId"/>.
    /// </summary>
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>The ceiling on items awaiting a callback (R3.4); null = they count against <see cref="MaxInFlight"/>.</summary>
    public int? MaxAwaitingAck { get; init; }

    /// <summary>
    /// Items awaiting a callback (R3.4): the sink's running count, re-counted from the items at
    /// each leadership slice, so it can lag a flush that has not landed yet.
    /// </summary>
    public long AwaitingAck { get; init; }

    /// <summary>The failure ratio at or above which the pacer suspends the campaign (R3.6); null = never.</summary>
    public double? SuspendOnFailureRatio { get; init; }

    /// <summary>The outcomes the failure ratio is measured over (R3.6).</summary>
    public int SuspendWindow { get; init; }

    /// <summary>The outcomes a window must hold before its ratio may suspend the campaign (R3.6).</summary>
    public int SuspendMinSample { get; init; }

    /// <summary>Why the pacer suspended the campaign (R3.6): the ratio, the window and the most frequent error code; null unless it is suspended.</summary>
    public string? SuspendedReason { get; init; }

    /// <summary>
    /// Failures over outcomes in the current window (R3.6), whether or not a ratio is set; null
    /// while the window holds no outcome. The window restarts at <see cref="SuspendWindow"/> outcomes
    /// only while a ratio is set.
    /// </summary>
    public double? FailureRatioNow { get; init; }

    /// <summary>
    /// The policy version (R3.5): incremented by every APPLIED throttle and by nothing else. A
    /// throttle that names the version it read is refused when the policy moved since.
    /// </summary>
    public long PolicyVersion { get; init; }
}

/// <summary>
/// What a versioned throttle did (R3.5). <paramref name="PolicyVersion"/> is the version the call
/// wrote, or the one it last read when it applied nothing (0 when no campaign exists); <paramref name="Stale"/> is true only when
/// the throttle named a version the policy no longer has, so a caller can map it (admin-contract R4: 412).
/// </summary>
/// <param name="Ok">True when the patch was applied.</param>
/// <param name="Message">The server's words: the outcome, or why it was refused.</param>
/// <param name="PolicyVersion">The policy version the call wrote, or last read when it applied nothing.</param>
/// <param name="Stale">True when the refusal is a stale expected version.</param>
public sealed record GoldpathCampaignThrottleResult(bool Ok, string Message, long PolicyVersion, bool Stale);

/// <summary>One failed item (the drill-down; REPLAY delegates to the jobs console).</summary>
public sealed record GoldpathCampaignFailedItem(long Seq, string? Error, DateTimeOffset? CompletedAt);

/// <summary>The LIVE policy patch (D6): null fields keep their current value.</summary>
public sealed record GoldpathCampaignThrottle(
    int? Tps = null,
    int? DailyQuota = null,
    int? MaxInFlight = null,
    TimeOnly? WindowStart = null,
    TimeOnly? WindowEnd = null,
    string? TimeZoneId = null,
    bool ClearDailyQuota = false,
    bool ClearWindow = false,
    IReadOnlyList<string>? ExcludedDays = null,
    DateOnly? EndDate = null,
    int? MaxAttempts = null,
    bool ClearExcludedDays = false,
    bool ClearEndDate = false,
    GoldpathCampaignPriority? Priority = null)
{
    /// <summary>
    /// Moves the scheduled start (R3.1); null keeps it. Only before the campaign has started; a
    /// start in the past means now, and a start after the end date's local day is refused.
    /// </summary>
    public DateTimeOffset? StartAt { get; init; }

    /// <summary>Clears the scheduled start (R3.1): the campaign starts as soon as its snapshot is ready.</summary>
    public bool ClearStartAt { get; init; }

    /// <summary>Sets the ceiling on items awaiting a callback (R3.4); null keeps it. Must be positive.</summary>
    public int? MaxAwaitingAck { get; init; }

    /// <summary>Clears that ceiling (R3.4): acceptances count against <c>MaxInFlight</c> again, as in R2.</summary>
    public bool ClearMaxAwaitingAck { get; init; }

    /// <summary>
    /// Sets the failure ratio that suspends the campaign (R3.6), in (0, 1]; null keeps it. Turning
    /// it on restarts the window, so outcomes from before the ratio existed do not judge the campaign.
    /// </summary>
    public double? SuspendOnFailureRatio { get; init; }

    /// <summary>Turns auto-suspend off (R3.6).</summary>
    public bool ClearSuspendOnFailureRatio { get; init; }

    /// <summary>The outcomes the failure ratio is measured over (R3.6); null keeps it.</summary>
    public int? SuspendWindow { get; init; }

    /// <summary>The outcomes a window must hold before its ratio may suspend (R3.6); null keeps it. At most <see cref="SuspendWindow"/>.</summary>
    public int? SuspendMinSample { get; init; }
}

/// <summary>
/// The campaign admin verbs (campaign RFC §4: create/pause/resume/abort/throttle — EVERY
/// mutating verb audited, the jobs console's iron rule kept). State transitions ride the
/// row's concurrency token: a verb that races the pacer loses loudly and reports it.
/// Item replay stays with the JOBS console (`replay-items`) — one repair discipline.
/// </summary>
public sealed class GoldpathCampaignAdminService<TContext>
    where TContext : DbContext
{
    /// <summary>How many conditional updates one throttle attempts before it reports the races it lost.</summary>
    private const int ThrottleAttempts = 8;

    private readonly GoldpathCampaignOptions _options;
    private readonly GoldpathCampaignEngine<TContext> _engine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathCampaignAdminService<TContext>> _logger;

    /// <summary>Registered by <c>AddGoldpathCampaign</c>.</summary>
    public GoldpathCampaignAdminService(
        GoldpathCampaignOptions options, GoldpathCampaignEngine<TContext> engine,
        IServiceScopeFactory scopeFactory, TimeProvider time,
        ILogger<GoldpathCampaignAdminService<TContext>> logger)
    {
        _options = options;
        _engine = engine;
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Creates a campaign INSTANCE over a registered type (operators launch, developers ship types — D1).</summary>
    public async Task<GoldpathAdminResult> CreateAsync(
        string type, string name, IReadOnlyDictionary<string, string> parameters,
        GoldpathCampaignThrottle? policy, string? tenant, string actor, CancellationToken ct)
    {
        if (!_options.TypeMap.ContainsKey(type))
        {
            return new GoldpathAdminResult(false,
                $"No campaign type named '{type}' — registered: {string.Join(", ", _options.TypeMap.Keys)}.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // #476: Oracle stores '' as NULL and refuses it on the NOT NULL column; elsewhere it would be an unnamed
            // campaign. The verb refuses it the same on every provider.
            return new GoldpathAdminResult(false, "A campaign needs a name — it is how operators and the audit trail tell campaigns apart.");
        }

        var now = _time.GetUtcNow();
        var defaults = _options.Type(type).DefaultPolicy;
        var effective = policy is null ? defaults : Apply(defaults, policy, now);
        if (effective.MaxAwaitingAck is <= 0)
        {
            // R3.4: a ceiling of zero would create a campaign that never releases, and say nothing. Checked on the
            // policy the campaign would run under, so a type default cannot carry one past it either.
            return new GoldpathAdminResult(false, "MaxAwaitingAck must be positive — omit it to keep acceptances under MaxInFlight.");
        }

        if ((StartRefusal(effective, now) ?? SuspendRefusal(effective)) is { } refusal)
        {
            return new GoldpathAdminResult(false, refusal);
        }

        using var scope = _scopeFactory.CreateScope();
        var campaign = await _engine.CreateAsync(scope.ServiceProvider, type, name, parameters, effective, tenant, actor, ct);
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await AuditAsync(db, campaign.Id, "create", actor,
            $"type={type} policy=({Describe(effective, effective.StartAt is not null, ShowsSuspend(effective, effective), effective.MaxAwaitingAck is not null)})", ct);
        return new GoldpathAdminResult(true, $"Campaign '{name}' created as {campaign.Id:N} ({type}).");
    }

    /// <summary>Every campaign with its live numbers (newest first; feeds the console list).</summary>
    public async Task<IReadOnlyList<GoldpathCampaignInfo>> ListAsync(string[]? state, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var query = db.Set<GoldpathCampaign>().AsNoTracking();
        var states = state is null
            ? new List<GoldpathCampaignState>()
            : [.. state.Select(value => Enum.TryParse<GoldpathCampaignState>(value, ignoreCase: true, out var parsed) ? parsed : (GoldpathCampaignState?)null)
                       .Where(parsed => parsed is not null)
                       .Select(parsed => parsed!.Value)];
        if (states.Count > 0)
        {
            // Several states are OR'd (contract R3); one behaves exactly as R2 did.
            query = query.Where(c => states.Contains(c.State));
        }

        var rows = await query.OrderByDescending(c => c.CreatedAt).Take(AdminPaging.Clamp(take)).ToListAsync(ct);
        return [.. rows.Select(Project)];
    }

    /// <summary>One campaign's full console view.</summary>
    public async Task<GoldpathCampaignInfo?> GetAsync(Guid id, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var row = await db.Set<GoldpathCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        return row is null ? null : Project(row);
    }

    /// <summary>The failed-item drill-down (replay rides the JOBS console — same repair discipline).</summary>
    public async Task<IReadOnlyList<GoldpathCampaignFailedItem>> GetFailedItemsAsync(Guid id, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == id && i.State == GoldpathCampaignItemState.Failed)
            .OrderBy(i => i.Seq)
            .Take(AdminPaging.Clamp(take))
            .Select(i => new GoldpathCampaignFailedItem(i.Seq, i.Error, i.CompletedAt))
            .ToListAsync(ct);
    }

    /// <summary>The audit trail of one campaign (who did what, newest first).</summary>
    public async Task<IReadOnlyList<GoldpathCampaignAudit>> GetAuditAsync(Guid id, int take, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        return await db.Set<GoldpathCampaignAudit>().AsNoTracking()
            .Where(a => a.CampaignId == id)
            .OrderByDescending(a => a.At)
            .Take(AdminPaging.Clamp(take))
            .ToListAsync(ct);
    }

    /// <summary>Pauses a live campaign (a Ready one too, R3.1): the pacer skips it next tick; in-flight items drain.</summary>
    public Task<GoldpathAdminResult> PauseAsync(Guid id, string actor, CancellationToken ct)
        => MutateAsync(id, "pause", actor, detail: null, campaign =>
        {
            if (campaign.State == GoldpathCampaignState.Suspended)
            {
                return $"'{campaign.Name}' is already stopped — the pacer suspended it ({campaign.SuspendedReason ?? "failure ratio"}). Resume it, or abort it.";
            }

            if (campaign.State is not (GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating or GoldpathCampaignState.Running
                or GoldpathCampaignState.Ready))
            {
                return $"'{campaign.Name}' is {campaign.State} — only a live campaign can pause.";
            }

            campaign.State = GoldpathCampaignState.Paused;
            campaign.LastVerb = $"paused by {actor}";
            return null;
        }, ct);

    /// <summary>
    /// Resumes a paused or suspended campaign (back to enumeration when the stream never finished,
    /// and to Ready while its scheduled start is still ahead, R3.1).
    /// R2.6: every Released item is marked due for the orphan sweep at once — the messages
    /// consumers refused during the pause come back on the next tick, not after
    /// <see cref="GoldpathCampaignOptions.OrphanReleaseAfter"/>.
    /// R3.6: resuming a suspended campaign clears its reason and restarts its failure-ratio
    /// window first, so the failures that suspended it cannot suspend it again.
    /// </summary>
    public async Task<GoldpathAdminResult> ResumeAsync(Guid id, string actor, CancellationToken ct)
    {
        await RestartWindowAsync(id, row => row.State == GoldpathCampaignState.Suspended, ct);
        var result = await MutateAsync(id, "resume", actor, detail: null, campaign =>
        {
            if (campaign.State is not (GoldpathCampaignState.Paused or GoldpathCampaignState.Suspended))
            {
                return $"'{campaign.Name}' is {campaign.State} — only a paused or suspended campaign can resume.";
            }

            campaign.SuspendedReason = null;

            campaign.State = !campaign.EnumerationComplete
                ? GoldpathCampaignState.Enumerating
                : GoldpathCampaignEngine<TContext>.PolicyOf(campaign).IsBeforeStart(_time.GetUtcNow())
                    ? GoldpathCampaignState.Ready
                    : GoldpathCampaignState.Running;
            campaign.LastVerb = $"resumed by {actor}";
            return null;
        }, ct);
        if (!result.Ok)
        {
            return result;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == id && i.State == GoldpathCampaignItemState.Released)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReleasedAt, (DateTimeOffset?)null), ct);
        return result;
    }

    /// <summary>
    /// Aborts a campaign: unreleased/unclaimed items and waiting retries are terminal-stamped
    /// Aborted; items a consumer already CLAIMED or a target system ACCEPTED drain gracefully
    /// through the sink and the pacer's sweeps (never yanked mid-call).
    /// </summary>
    public async Task<GoldpathAdminResult> AbortAsync(Guid id, string reason, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new GoldpathAdminResult(false, "Abort requires a reason — it becomes the evidence.");
        }

        var result = await MutateAsync(id, "abort", actor, detail: reason, campaign =>
        {
            if (campaign.State is GoldpathCampaignState.Completed or GoldpathCampaignState.CompletedWithFailures
                or GoldpathCampaignState.Aborted or GoldpathCampaignState.ExpiredIncomplete)
            {
                return $"'{campaign.Name}' is already terminal ({campaign.State}).";
            }

            campaign.State = GoldpathCampaignState.Aborted;
            campaign.CompletedAt = _time.GetUtcNow();
            campaign.LastVerb = $"aborted by {actor}: {reason}";
            return null;
        }, ct);
        if (!result.Ok)
        {
            return result;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // Unsent work is everything nobody is executing: never released, released but
        // unclaimed, and a retry waiting on its rung (R2 review #4 — the pacer never visits
        // an aborted campaign, so a waiting retry left as it was would wait forever).
        var stamped = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == id
                && (i.State == GoldpathCampaignItemState.Pending || i.State == GoldpathCampaignItemState.Released
                    || i.State == GoldpathCampaignItemState.AwaitingRetry))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Aborted)
                .SetProperty(i => i.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(i => i.CompletedAt, _time.GetUtcNow()), ct);
        _logger.LogInformation("Campaign {CampaignId} aborted by {Actor}: {Stamped} unsent items terminal-stamped.", id, actor, stamped);
        return new GoldpathAdminResult(true, $"Campaign aborted; {stamped} unsent items stamped Aborted (claimed items drain).");
    }

    /// <summary>
    /// The LIVE throttle (D6): patches policy on the row; takes effect within one leader tick.
    /// Last writer wins (R3.5): the shipped shape, kept for scripts and R2 clients.
    /// </summary>
    public async Task<GoldpathAdminResult> ThrottleAsync(Guid id, GoldpathCampaignThrottle patch, string actor, CancellationToken ct)
    {
        var result = await ThrottleAsync(id, patch, expectedPolicyVersion: null, actor, ct);
        return new GoldpathAdminResult(result.Ok, result.Message);
    }

    /// <summary>
    /// The LIVE throttle with the campaign half of optimistic concurrency (R3.5). Each attempt is
    /// one conditional update — the patched policy columns and <c>PolicyVersion + 1</c>, where the
    /// row still has the state and the policy version this call read. With
    /// <paramref name="expectedPolicyVersion"/>, a policy that moved since is refused (and audited
    /// as <c>throttle-refused</c>); without it, a lost race is re-read and re-applied, so the last
    /// writer wins and the version still moves once per applied throttle.
    /// </summary>
    public async Task<GoldpathCampaignThrottleResult> ThrottleAsync(
        Guid id, GoldpathCampaignThrottle patch, long? expectedPolicyVersion, string actor, CancellationToken ct)
    {
        var invalid = ThrottleRefusal(patch);
        if (invalid is null && patch.SuspendOnFailureRatio is not null && !patch.ClearSuspendOnFailureRatio)
        {
            // R3.6: a ratio switched on judges outcomes from now on. The window restarts BEFORE the
            // policy is written, so no tick can read the new ratio against the old counters; a
            // throttle refused below leaves a restarted window and no ratio, which changes nothing. Only a
            // live campaign with no ratio yet, whose new suspend policy will stand, is touched at all;
            // a versioned throttle whose expected version is already stale touches nothing (R3.5).
            var now = _time.GetUtcNow();
            await RestartWindowAsync(id, row => row.SuspendOnFailureRatio is null && !IsTerminal(row.State)
                && (expectedPolicyVersion is null || row.PolicyVersion == expectedPolicyVersion)
                && SuspendRefusal(Apply(GoldpathCampaignEngine<TContext>.PolicyOf(row), patch, now)) is null, ct);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var lastVersion = 0L;
        for (var attempt = 0; attempt < ThrottleAttempts; attempt++)
        {
            // Read without tracking: the throttle writes through its own conditional update, never
            // through SaveChanges, so the State token cannot fail it and it cannot clobber the pacer.
            var campaign = await db.Set<GoldpathCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
            if (campaign is null)
            {
                // A nonsense patch is named before a missing campaign, exactly as the shipped verb did.
                return new GoldpathCampaignThrottleResult(false, invalid ?? $"No campaign {id:N}.", 0, Stale: false);
            }

            var readState = campaign.State;
            var readVersion = campaign.PolicyVersion;
            lastVersion = readVersion;
            if (invalid is not null)
            {
                return new GoldpathCampaignThrottleResult(false, invalid, readVersion, Stale: false);
            }

            if (expectedPolicyVersion is { } expected && expected != readVersion)
            {
                return await StaleAsync(db, campaign, expected, actor, ct);
            }

            if (ApplyThrottle(campaign, patch, actor, out var verb) is { } refusal)
            {
                return new GoldpathCampaignThrottleResult(false, refusal, readVersion, Stale: false);
            }

            var applied = await db.Set<GoldpathCampaign>()
                .Where(c => c.Id == id && c.State == readState && c.PolicyVersion == readVersion)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Tps, campaign.Tps)
                    .SetProperty(c => c.DailyQuota, campaign.DailyQuota)
                    .SetProperty(c => c.MaxInFlight, campaign.MaxInFlight)
                    .SetProperty(c => c.WindowStart, campaign.WindowStart)
                    .SetProperty(c => c.WindowEnd, campaign.WindowEnd)
                    .SetProperty(c => c.TimeZoneId, campaign.TimeZoneId)
                    .SetProperty(c => c.ExcludedDays, campaign.ExcludedDays)
                    .SetProperty(c => c.EndDate, campaign.EndDate)
                    .SetProperty(c => c.MaxAttempts, campaign.MaxAttempts)
                    .SetProperty(c => c.Priority, campaign.Priority)
                    .SetProperty(c => c.StartAt, campaign.StartAt)
                    .SetProperty(c => c.MaxAwaitingAck, campaign.MaxAwaitingAck)
                    .SetProperty(c => c.SuspendOnFailureRatio, campaign.SuspendOnFailureRatio)
                    .SetProperty(c => c.SuspendWindow, campaign.SuspendWindow)
                    .SetProperty(c => c.SuspendMinSample, campaign.SuspendMinSample)
                    .SetProperty(c => c.LastVerb, campaign.LastVerb)
                    .SetProperty(c => c.PolicyVersion, c => c.PolicyVersion + 1), ct);
            if (applied == 1)
            {
                var version = readVersion + 1;
                // The audit detail stays the verb text exactly (old -> new), bounded to the audit row's
                // 1024 rather than LastVerb's 512 (R3.6); the version rides the result.
                await AuditAsync(db, id, "throttle", actor, Bound(verb, 1024), ct);
                _logger.LogInformation("Campaign {CampaignId}: throttle by {Actor} (policy version {PolicyVersion}).", id, actor, version);
                return new GoldpathCampaignThrottleResult(true,
                    $"throttle: '{campaign.Name}' is now {readState}.",
                    version, Stale: false);
            }

            // Lost the race: the state or the policy moved between the read and the update. Re-read;
            // a versioned throttle is refused there if it was the policy, and re-applied if it was only the state.
        }

        return new GoldpathCampaignThrottleResult(false,
            string.Create(CultureInfo.InvariantCulture,
                $"The throttle lost {ThrottleAttempts} races in a row to concurrent changes on campaign {id:N} — nothing was applied; re-read and retry."),
            lastVersion, Stale: false);
    }

    /// <summary>Why a patch is nonsense on any campaign, or null (checked before the row matters).</summary>
    private static string? ThrottleRefusal(GoldpathCampaignThrottle patch)
    {
        if (patch.Tps is <= 0 || patch.MaxInFlight is <= 0 || patch.DailyQuota is <= 0 || patch.MaxAttempts is <= 0
            || patch.SuspendWindow is <= 0 || patch.SuspendMinSample is <= 0 || patch.MaxAwaitingAck is <= 0)
        {
            return "Throttle values must be positive.";
        }

        if (patch.SuspendOnFailureRatio is { } ratio && !patch.ClearSuspendOnFailureRatio
            && SuspendRefusal(new GoldpathCampaignPolicy(1, null, 1, null, null, "UTC") { SuspendOnFailureRatio = ratio }) is { } ratioRefusal)
        {
            // Refused before anything is written: switching a ratio on restarts the window first.
            return ratioRefusal;
        }

        if (patch.ExcludedDays is { } days)
        {
            var unknown = days.Where(day => !Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out _)).ToArray();
            if (unknown.Length > 0)
            {
                return $"Unknown day '{unknown[0]}' — use English day names (Saturday, Sunday…).";
            }

            if (GoldpathCampaignPolicy.ParseDays(string.Join(",", days)).Count == 7)
            {
                // Seven excluded days is a pause wearing a policy's clothes — say so.
                return "Every day excluded would never release — pause the campaign instead.";
            }
        }

        if (patch.TimeZoneId is { } tz)
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(tz);
            }
            catch (TimeZoneNotFoundException)
            {
                return $"Unknown timezone '{tz}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// Applies the patch to a detached read of the row, or says why it cannot stand there.
    /// <paramref name="verb"/> is the full old -> new text for the audit row (up to 1024); the row's
    /// own <see cref="GoldpathCampaign.LastVerb"/> keeps it bounded to its 512.
    /// </summary>
    private string? ApplyThrottle(GoldpathCampaign campaign, GoldpathCampaignThrottle patch, string actor, out string verb)
    {
        verb = "";
        if (IsTerminal(campaign.State))
        {
            return $"'{campaign.Name}' is terminal ({campaign.State}) — nothing left to throttle.";
        }

        var now = _time.GetUtcNow();
        if ((patch.StartAt is not null || patch.ClearStartAt)
            && (campaign.State == GoldpathCampaignState.Running || campaign.ReleasedThrough > 0))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"'{campaign.Name}' has already started ({campaign.State}, {campaign.ReleasedThrough} released) — a scheduled start moves only before the campaign starts; to hold it, pause it, or lower its Tps.");
        }

        var current = GoldpathCampaignEngine<TContext>.PolicyOf(campaign);
        var effective = Apply(current, patch, now);
        if ((StartRefusal(effective, now) ?? SuspendRefusal(effective)) is { } refusal)
        {
            return refusal;
        }

        var showStart = current.StartAt is not null || effective.StartAt is not null;
        var showSuspend = ShowsSuspend(current, effective);
        var showAck = current.MaxAwaitingAck is not null || effective.MaxAwaitingAck is not null;
        var before = Describe(current, showStart, showSuspend, showAck);
        campaign.Tps = effective.Tps;
        campaign.DailyQuota = effective.DailyQuota;
        campaign.MaxInFlight = effective.MaxInFlight;
        campaign.WindowStart = effective.WindowStart;
        campaign.WindowEnd = effective.WindowEnd;
        campaign.TimeZoneId = effective.TimeZoneId;
        campaign.ExcludedDays = effective.ExcludedDays.Count == 0 ? null : string.Join(",", effective.ExcludedDays);
        campaign.EndDate = effective.EndDate;
        campaign.MaxAttempts = effective.MaxAttempts;
        campaign.Priority = effective.Priority;
        campaign.StartAt = effective.StartAt;
        campaign.SuspendOnFailureRatio = effective.SuspendOnFailureRatio;
        campaign.SuspendWindow = effective.SuspendWindow;
        campaign.SuspendMinSample = effective.SuspendMinSample;
        campaign.MaxAwaitingAck = effective.MaxAwaitingAck;
        verb = $"throttled by {actor}: {before} -> {Describe(effective, showStart, showSuspend, showAck)}";
        // Both sides of a wide policy can outgrow the row's 512; the audit row keeps up to 1024.
        campaign.LastVerb = Bound(verb, 512);
        return null;
    }

    /// <summary>True for the four states no verb but replay moves a campaign out of.</summary>
    private static bool IsTerminal(GoldpathCampaignState state)
        => state is GoldpathCampaignState.Completed or GoldpathCampaignState.CompletedWithFailures
            or GoldpathCampaignState.Aborted or GoldpathCampaignState.ExpiredIncomplete;

    /// <summary>Cuts a text to a column's length.</summary>
    internal static string Bound(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>
    /// Restarts a campaign's failure-ratio window (R3.6): subtracts the counters it read and stamps
    /// the start, never zeroes them, so an outcome the sink flushes in between is kept. Only when the
    /// row it read satisfies <paramref name="applies"/>, so it touches only the campaign the verb is
    /// about to change.
    /// </summary>
    private async Task RestartWindowAsync(Guid id, Func<GoldpathCampaign, bool> applies, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var read = await db.Set<GoldpathCampaign>().AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (read is null || !applies(read))
        {
            return;
        }

        await GoldpathCampaignEngine<TContext>.RestartWindowAsync(
            db, id, read.WindowOutcomes, read.WindowFailures, _time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Refuses a throttle whose expected version the policy no longer has (R3.5), in the server's
    /// words — the current version, and who moved it when — and audits the refusal with both versions.
    /// </summary>
    private async Task<GoldpathCampaignThrottleResult> StaleAsync(
        TContext db, GoldpathCampaign campaign, long expected, string actor, CancellationToken ct)
    {
        // Who moved it is the newest applied throttle's audit row. That row is written just after
        // its update commits, so a refusal landing in that instant names the throttle before it;
        // the version it reports is the row's own, always.
        var last = await db.Set<GoldpathCampaignAudit>().AsNoTracking()
            .Where(a => a.CampaignId == campaign.Id && a.Action == "throttle")
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);
        var message = last is null
            ? string.Create(CultureInfo.InvariantCulture,
                $"You named policy version {expected} of '{campaign.Name}', but its policy has never changed (version {campaign.PolicyVersion}) — re-read and retry.")
            : string.Create(CultureInfo.InvariantCulture,
                $"The policy of '{campaign.Name}' changed since you read it (version {campaign.PolicyVersion}, by {last.Actor} at {last.At.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z; you read version {expected}) — re-read and retry.");
        await AuditAsync(db, campaign.Id, "throttle-refused", actor,
            string.Create(CultureInfo.InvariantCulture, $"expected policy version {expected}, current {campaign.PolicyVersion}"), ct);
        _logger.LogInformation("Campaign {CampaignId}: throttle by {Actor} refused stale (expected policy version {Expected}, current {Current}).",
            campaign.Id, actor, expected, campaign.PolicyVersion);
        return new GoldpathCampaignThrottleResult(false, message, campaign.PolicyVersion, Stale: true);
    }

    private async Task<GoldpathAdminResult> MutateAsync(
        Guid id, string action, string actor, string? detail,
        Func<GoldpathCampaign, string?> mutate, CancellationToken ct,
        Func<GoldpathCampaign, string?>? detailFactory = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var campaign = await db.Set<GoldpathCampaign>().SingleOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return new GoldpathAdminResult(false, $"No campaign {id:N}.");
        }

        if (mutate(campaign) is { } refusal)
        {
            return new GoldpathAdminResult(false, refusal);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new GoldpathAdminResult(false,
                $"'{campaign.Name}' changed state while the verb ran (the pacer or another operator) — re-read and retry.");
        }

        await AuditAsync(db, id, action, actor, detailFactory?.Invoke(campaign) ?? detail, ct);
        _logger.LogInformation("Campaign {CampaignId}: {Action} by {Actor}.", id, action, actor);
        return new GoldpathAdminResult(true, $"{action}: '{campaign.Name}' is now {campaign.State}.");
    }

    private async Task AuditAsync(TContext db, Guid campaignId, string action, string actor, string? detail, CancellationToken ct)
    {
        db.Set<GoldpathCampaignAudit>().Add(new GoldpathCampaignAudit
        {
            At = _time.GetUtcNow(),
            Actor = actor,
            Action = action,
            CampaignId = campaignId,
            Detail = detail,
        });
        await db.SaveChangesAsync(ct);
    }

    private GoldpathCampaignInfo Project(GoldpathCampaign c)
    {
        var policy = GoldpathCampaignEngine<TContext>.PolicyOf(c);
        var inFlight = c.ReleasedThrough - c.SucceededCount - c.FailedCount;
        var remaining = c.EnumeratedThrough - c.ReleasedThrough;
        double? eta = c.State == GoldpathCampaignState.Running && c.Tps > 0 && remaining > 0
            ? remaining / (double)c.Tps
            : null;
        return new GoldpathCampaignInfo(
            c.Id, c.Type, c.Name, c.State.ToString(),
            c.EnumeratedThrough, c.EnumerationComplete, c.ReleasedThrough,
            c.SucceededCount, c.FailedCount, inFlight, remaining,
            c.Tps, c.DailyQuota, c.ReleasedToday, c.MaxInFlight,
            c.WindowStart, c.WindowEnd, c.TimeZoneId,
            [.. policy.ExcludedDays.Select(day => day.ToString())], c.EndDate, c.MaxAttempts, c.Priority,
            policy.IsWindowOpen(_time.GetUtcNow()) && policy.IsDayAllowed(_time.GetUtcNow()),
            eta, c.CreatedAt, c.CreatedBy, c.CompletedAt, c.LastVerb, c.Tenant)
        {
            StartAt = c.StartAt,
            MaxAwaitingAck = c.MaxAwaitingAck,
            // The stored count can briefly read below zero or above in flight (an upgraded row settled before the first
            // slice's re-count, or a re-count racing a flush); what an operator reads is held inside [0, in flight].
            AwaitingAck = Math.Clamp(c.AwaitingAckCount, 0, Math.Max(0, inFlight)),
            SuspendOnFailureRatio = policy.SuspendOnFailureRatio,
            SuspendWindow = policy.SuspendWindow,
            SuspendMinSample = policy.SuspendMinSample,
            SuspendedReason = c.SuspendedReason,
            FailureRatioNow = c.WindowOutcomes > 0 ? c.WindowFailures / (double)c.WindowOutcomes : null,
            PolicyVersion = c.PolicyVersion,
        };
    }

    private static GoldpathCampaignPolicy Apply(GoldpathCampaignPolicy current, GoldpathCampaignThrottle patch, DateTimeOffset now)
        => new(
            patch.Tps ?? current.Tps,
            patch.ClearDailyQuota ? null : patch.DailyQuota ?? current.DailyQuota,
            patch.MaxInFlight ?? current.MaxInFlight,
            patch.ClearWindow ? null : patch.WindowStart ?? current.WindowStart,
            patch.ClearWindow ? null : patch.WindowEnd ?? current.WindowEnd,
            patch.TimeZoneId ?? current.TimeZoneId)
        {
            ExcludedDays = patch.ClearExcludedDays
                ? []
                : patch.ExcludedDays is { } days
                    ? GoldpathCampaignPolicy.ParseDays(string.Join(",", days))
                    : current.ExcludedDays,
            EndDate = patch.ClearEndDate ? null : patch.EndDate ?? current.EndDate,
            MaxAttempts = patch.MaxAttempts ?? current.MaxAttempts,
            Priority = patch.Priority ?? current.Priority,
            // R3.1: a start in the past means now; the stored instant is UTC.
            StartAt = patch.ClearStartAt
                ? null
                : patch.StartAt is { } start
                    ? (start < now ? now : start).ToUniversalTime()
                    : current.StartAt,
            SuspendOnFailureRatio = patch.ClearSuspendOnFailureRatio ? null : patch.SuspendOnFailureRatio ?? current.SuspendOnFailureRatio,
            SuspendWindow = patch.SuspendWindow ?? current.SuspendWindow,
            SuspendMinSample = patch.SuspendMinSample ?? current.SuspendMinSample,
            MaxAwaitingAck = patch.ClearMaxAwaitingAck ? null : patch.MaxAwaitingAck ?? current.MaxAwaitingAck,
        };

    /// <summary>
    /// Why a policy's auto-suspend cannot stand (R3.6), or null: the ratio lies in (0, 1], the window
    /// and the minimum sample are positive, and the sample fits in the window — a window that restarts
    /// before it holds the minimum sample could never suspend anything.
    /// </summary>
    private static string? SuspendRefusal(GoldpathCampaignPolicy policy)
    {
        if (policy.SuspendOnFailureRatio is { } ratio && !(ratio > 0 && ratio <= 1))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"The failure ratio {ratio} is outside (0, 1] — 0.3 suspends the campaign when three outcomes in ten fail.");
        }

        if (policy.SuspendWindow <= 0 || policy.SuspendMinSample <= 0)
        {
            return "The suspend window and the minimum sample must be positive.";
        }

        return policy.SuspendMinSample > policy.SuspendWindow
            ? string.Create(CultureInfo.InvariantCulture,
                $"The minimum sample {policy.SuspendMinSample} is larger than the window {policy.SuspendWindow} — the window would restart before it could ever suspend. Lower the sample or widen the window.")
            : null;
    }

    /// <summary>Whether the audit's policy text names auto-suspend (R3.6): when either side sets a ratio, or the window or sample moved.</summary>
    private static bool ShowsSuspend(GoldpathCampaignPolicy before, GoldpathCampaignPolicy after)
        => before.SuspendOnFailureRatio is not null || after.SuspendOnFailureRatio is not null
            || before.SuspendWindow != after.SuspendWindow || before.SuspendMinSample != after.SuspendMinSample
            || after.SuspendWindow != GoldpathCampaignPolicy.DefaultSuspendWindow
            || after.SuspendMinSample != GoldpathCampaignPolicy.DefaultSuspendMinSample;

    /// <summary>
    /// Why a policy's scheduled start cannot stand (R3.1), or null: a start still ahead must fall
    /// on or before the end date's local day, in the policy's time zone.
    /// </summary>
    private static string? StartRefusal(GoldpathCampaignPolicy policy, DateTimeOffset now)
    {
        if (!policy.IsBeforeStart(now) || policy.EndDate is not { } end || policy.StartAt is not { } start)
        {
            return null;
        }

        var startDay = policy.LocalDay(start);
        return startDay > end
            ? string.Create(CultureInfo.InvariantCulture,
                $"The start {start.UtcDateTime:yyyy-MM-ddTHH:mm:ss}Z falls on {startDay:yyyy-MM-dd} in {policy.TimeZoneId}, after the end date {end:yyyy-MM-dd} — the campaign would expire before it starts. Choose an earlier start or move the end date.")
            : null;
    }

    /// <summary>
    /// The audit's policy text. <paramref name="showStart"/> adds the scheduled start (R3.1) when
    /// either side of the change has one, so a policy without a start reads exactly as before;
    /// <paramref name="showSuspend"/> does the same for auto-suspend (R3.6) and
    /// <paramref name="showAck"/> for the acknowledgement ceiling (R3.4).
    /// </summary>
    private static string Describe(GoldpathCampaignPolicy p, bool showStart = false, bool showSuspend = false, bool showAck = false)
        => DescribeCore(p, showStart) + (showSuspend
            ? string.Create(CultureInfo.InvariantCulture,
                $" suspendOn={p.SuspendOnFailureRatio?.ToString("0.###", CultureInfo.InvariantCulture) ?? "off"} suspendWindow={p.SuspendWindow} suspendMinSample={p.SuspendMinSample}")
            : "") + (showAck
            ? string.Create(CultureInfo.InvariantCulture, $" maxAwaitingAck={p.MaxAwaitingAck?.ToString(CultureInfo.InvariantCulture) ?? "none"}")
            : "");

    private static string DescribeCore(GoldpathCampaignPolicy p, bool showStart)
        => string.Create(CultureInfo.InvariantCulture,
            $"tps={p.Tps} quota={p.DailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "none"} maxInFlight={p.MaxInFlight} window={(p.WindowStart is null ? "always" : $"{p.WindowStart}-{p.WindowEnd} {p.TimeZoneId}")} excludedDays={(p.ExcludedDays.Count == 0 ? "none" : string.Join("+", p.ExcludedDays))} endDate={p.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "open"} maxAttempts={p.MaxAttempts} priority={p.Priority}{(showStart ? $" start={(p.StartAt is { } s ? s.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z" : "when-ready")}" : "")}");
}
