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
    string? Tenant);

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
    GoldpathCampaignPriority? Priority = null);

/// <summary>
/// The campaign admin verbs (campaign RFC §4: create/pause/resume/abort/throttle — EVERY
/// mutating verb audited, the jobs console's iron rule kept). State transitions ride the
/// row's concurrency token: a verb that races the pacer loses loudly and reports it.
/// Item replay stays with the JOBS console (`replay-items`) — one repair discipline.
/// </summary>
public sealed class GoldpathCampaignAdminService<TContext>
    where TContext : DbContext
{
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

        using var scope = _scopeFactory.CreateScope();
        var defaults = _options.Type(type).DefaultPolicy;
        var effective = policy is null ? defaults : Apply(defaults, policy);
        var campaign = await _engine.CreateAsync(scope.ServiceProvider, type, name, parameters, effective, tenant, actor, ct);
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        await AuditAsync(db, campaign.Id, "create", actor,
            $"type={type} policy=({Describe(effective)})", ct);
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

    /// <summary>Pauses a live campaign: the pacer skips it next tick; in-flight items drain.</summary>
    public Task<GoldpathAdminResult> PauseAsync(Guid id, string actor, CancellationToken ct)
        => MutateAsync(id, "pause", actor, detail: null, campaign =>
        {
            if (campaign.State is not (GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating or GoldpathCampaignState.Running))
            {
                return $"'{campaign.Name}' is {campaign.State} — only a live campaign can pause.";
            }

            campaign.State = GoldpathCampaignState.Paused;
            campaign.LastVerb = $"paused by {actor}";
            return null;
        }, ct);

    /// <summary>
    /// Resumes a paused campaign (back to enumeration when the stream never finished).
    /// R2.6: every Released item is marked due for the orphan sweep at once — the messages
    /// consumers refused during the pause come back on the next tick, not after
    /// <see cref="GoldpathCampaignOptions.OrphanReleaseAfter"/>.
    /// </summary>
    public async Task<GoldpathAdminResult> ResumeAsync(Guid id, string actor, CancellationToken ct)
    {
        var result = await MutateAsync(id, "resume", actor, detail: null, campaign =>
        {
            if (campaign.State != GoldpathCampaignState.Paused)
            {
                return $"'{campaign.Name}' is {campaign.State} — only a paused campaign can resume.";
            }

            campaign.State = campaign.EnumerationComplete ? GoldpathCampaignState.Running : GoldpathCampaignState.Enumerating;
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
    /// Aborts a campaign: unreleased/unclaimed items are terminal-stamped Aborted; items a
    /// consumer already CLAIMED drain gracefully through the sink (never yanked mid-call).
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
        var stamped = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == id
                && (i.State == GoldpathCampaignItemState.Pending || i.State == GoldpathCampaignItemState.Released))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Aborted)
                .SetProperty(i => i.CompletedAt, _time.GetUtcNow()), ct);
        _logger.LogInformation("Campaign {CampaignId} aborted by {Actor}: {Stamped} unsent items terminal-stamped.", id, actor, stamped);
        return new GoldpathAdminResult(true, $"Campaign aborted; {stamped} unsent items stamped Aborted (claimed items drain).");
    }

    /// <summary>The LIVE throttle (D6): patches policy on the row; takes effect within one leader tick.</summary>
    public Task<GoldpathAdminResult> ThrottleAsync(Guid id, GoldpathCampaignThrottle patch, string actor, CancellationToken ct)
    {
        if (patch.Tps is <= 0 || patch.MaxInFlight is <= 0 || patch.DailyQuota is <= 0 || patch.MaxAttempts is <= 0)
        {
            return Task.FromResult(new GoldpathAdminResult(false, "Throttle values must be positive."));
        }

        if (patch.ExcludedDays is { } days)
        {
            var unknown = days.Where(day => !Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out _)).ToArray();
            if (unknown.Length > 0)
            {
                return Task.FromResult(new GoldpathAdminResult(false, $"Unknown day '{unknown[0]}' — use English day names (Saturday, Sunday…)."));
            }

            if (GoldpathCampaignPolicy.ParseDays(string.Join(",", days)).Count == 7)
            {
                // Seven excluded days is a pause wearing a policy's clothes — say so.
                return Task.FromResult(new GoldpathAdminResult(false, "Every day excluded would never release — pause the campaign instead."));
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
                return Task.FromResult(new GoldpathAdminResult(false, $"Unknown timezone '{tz}'."));
            }
        }

        return MutateAsync(id, "throttle", actor, detail: null, campaign =>
        {
            if (campaign.State is GoldpathCampaignState.Completed or GoldpathCampaignState.CompletedWithFailures
                or GoldpathCampaignState.Aborted or GoldpathCampaignState.ExpiredIncomplete)
            {
                return $"'{campaign.Name}' is terminal ({campaign.State}) — nothing left to throttle.";
            }

            var before = Describe(GoldpathCampaignEngine<TContext>.PolicyOf(campaign));
            var effective = Apply(GoldpathCampaignEngine<TContext>.PolicyOf(campaign), patch);
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
            campaign.LastVerb = $"throttled by {actor}: {before} -> {Describe(effective)}";
            return null;
        }, ct, detailFactory: campaign => campaign.LastVerb);
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
            eta, c.CreatedAt, c.CreatedBy, c.CompletedAt, c.LastVerb, c.Tenant);
    }

    private static GoldpathCampaignPolicy Apply(GoldpathCampaignPolicy current, GoldpathCampaignThrottle patch)
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
        };

    private static string Describe(GoldpathCampaignPolicy p)
        => string.Create(CultureInfo.InvariantCulture,
            $"tps={p.Tps} quota={p.DailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "none"} maxInFlight={p.MaxInFlight} window={(p.WindowStart is null ? "always" : $"{p.WindowStart}-{p.WindowEnd} {p.TimeZoneId}")} excludedDays={(p.ExcludedDays.Count == 0 ? "none" : string.Join("+", p.ExcludedDays))} endDate={p.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "open"} maxAttempts={p.MaxAttempts} priority={p.Priority}");
}
