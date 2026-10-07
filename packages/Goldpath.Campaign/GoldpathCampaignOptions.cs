using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>
/// The app's execution hook: one claimed target. Throwing marks the item Failed (the
/// repair story applies). The item was CLAIMED (persisted) before this runs — a broker
/// redelivery cannot double-execute it (constraint 2). Do NOT call SaveChanges here:
/// outcomes flow through the batching sink (constraint 4, GP1702).
/// </summary>
public interface IGoldpathCampaignItemHandler<in TTarget>
    where TTarget : class
{
    /// <summary>Executes one target.</summary>
    Task ExecuteAsync(TTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken);
}

/// <summary>Ambient facts handed to the item handler.</summary>
public sealed class GoldpathCampaignItemContext
{
    internal GoldpathCampaignItemContext(Guid campaignId, long seq, string type, string? tenant, int attempt, bool replay, IServiceProvider services)
    {
        CampaignId = campaignId;
        Seq = seq;
        Type = type;
        Tenant = tenant;
        Attempt = attempt;
        Replay = replay;
        Services = services;
    }

    /// <summary>The owning campaign instance.</summary>
    public Guid CampaignId { get; }

    /// <summary>The item's sequence (the repair coordinate).</summary>
    public long Seq { get; }

    /// <summary>Which attempt this execution is, 1-based (R2.1): the ladder's rung and the idempotency key's suffix.</summary>
    public int Attempt { get; }

    /// <summary>
    /// `{campaignId:N}#{seq}#{attempt}` — hand it to the target system (R2.1) so a redelivered
    /// request is recognized there as the same attempt, never executed twice.
    /// </summary>
    public string IdempotencyKey
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{CampaignId:N}#{Seq}#{Attempt}");

    /// <summary>The campaign-type key.</summary>
    public string Type { get; }

    /// <summary>The campaign's tenant, when tenant-bound.</summary>
    public string? Tenant { get; }

    /// <summary>True when this invocation is an admin replay of a failed item.</summary>
    public bool Replay { get; }

    /// <summary>Scoped services of the executing consumer.</summary>
    public IServiceProvider Services { get; }
}

/// <summary>The live policy snapshot the pacer evaluates each tick (all fields runtime-adjustable, D6).</summary>
public sealed record GoldpathCampaignPolicy(
    int Tps,
    int? DailyQuota,
    int MaxInFlight,
    TimeOnly? WindowStart,
    TimeOnly? WindowEnd,
    string TimeZoneId)
{
    /// <summary>Days on which nothing releases (R1.1), evaluated in <see cref="TimeZoneId"/>.</summary>
    public IReadOnlyList<DayOfWeek> ExcludedDays { get; init; } = [];

    /// <summary>Last local calendar day releases are allowed on (R1.2); null = open-ended.</summary>
    public DateOnly? EndDate { get; init; }

    /// <summary>Attempts per item before the repair queue (R1.3); 1 = today's behavior.</summary>
    public int MaxAttempts { get; init; } = 1;

    /// <summary>Share of a contended shared ceiling (R2.5); Normal unless an operator says otherwise.</summary>
    public GoldpathCampaignPriority Priority { get; init; } = GoldpathCampaignPriority.Normal;

    /// <summary>True when <paramref name="utcNow"/> falls inside the send window (always true when no window).</summary>
    public bool IsWindowOpen(DateTimeOffset utcNow)
    {
        if (WindowStart is null || WindowEnd is null)
        {
            return true;
        }

        var local = TimeOnly.FromTimeSpan(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).TimeOfDay);
        return WindowStart <= WindowEnd
            ? local >= WindowStart && local < WindowEnd
            : local >= WindowStart || local < WindowEnd;   // overnight window (22:00–06:00)
    }

    /// <summary>The policy-timezone calendar day (quota accounting).</summary>
    public DateOnly LocalDay(DateTimeOffset utcNow)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).Date);

    /// <summary>False on an excluded day (R1.1) — "weekends are off-limits" without a Friday-night human.</summary>
    public bool IsDayAllowed(DateTimeOffset utcNow)
        => ExcludedDays.Count == 0
            || !ExcludedDays.Contains(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId)).DayOfWeek);

    /// <summary>True once the local day is PAST <see cref="EndDate"/> (R1.2) — the day itself still counts.</summary>
    public bool IsExpired(DateTimeOffset utcNow)
        => EndDate is { } end && LocalDay(utcNow) > end;

    /// <summary>
    /// The R1 ladder's two rungs (30s then 2m), kept as the documented default shape.
    /// R2 reads the rungs from <see cref="GoldpathCampaignOptions.RetryBackoff"/> (configurable,
    /// jittered) — this static stays for callers that pinned the R1 numbers.
    /// </summary>
    public static TimeSpan RetryBackoff(int attemptsSoFar)
        => attemptsSoFar <= 1 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(2);

    /// <summary>
    /// Parses the model's CSV day set — unknown tokens are ignored (as filters are) and
    /// duplicates COLLAPSE, so "Saturday,Saturday,…" can never dodge a distinct-count
    /// rule downstream (review R3 on the all-seven refusal).
    /// </summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string? csv)
        => csv is null
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(token => Enum.TryParse<DayOfWeek>(token, ignoreCase: true, out var day) ? day : (DayOfWeek?)null)
                     .Where(day => day is not null)
                     .Select(day => day!.Value)
                     .Distinct()];
}

/// <summary>One registered campaign type — code, shipped through PRs (D1). Closures baked.</summary>
public sealed class GoldpathCampaignType
{
    internal GoldpathCampaignType(string key) => Key = key;

    /// <summary>The registration key instances reference.</summary>
    public string Key { get; }

    /// <summary>The enumeration ceiling — MANDATORY (an unbounded L4 enumeration is an outage, GP1701).</summary>
    public long MaxTargets { get; internal set; }

    /// <summary>
    /// Release ceiling per second across EVERY campaign of this type (R2.4); null = none.
    /// This is the TARGET SYSTEM's protection — an SMS gateway or a device-management
    /// core has a rate it can take, and three campaigns at once must not exceed it together.
    /// </summary>
    public int? MaxTps { get; internal set; }

    /// <summary>Default policy for new instances (operators may override per instance).</summary>
    public GoldpathCampaignPolicy DefaultPolicy { get; internal set; }
        = new(Tps: 50, DailyQuota: null, MaxInFlight: 1_000, WindowStart: null, WindowEnd: null, TimeZoneId: "UTC");

    internal Func<IServiceProvider, IReadOnlyDictionary<string, string>, IAsyncEnumerable<object>> Enumerate { get; set; } = null!;

    /// <summary>True when the type resumes enumeration by KEY after a takeover (R2.7) rather than by count.</summary>
    public bool ResumesByKey => KeyOf is not null;

    internal Func<IServiceProvider, IReadOnlyDictionary<string, string>, string?, IAsyncEnumerable<object>>? EnumerateAfter { get; set; }

    internal Func<object, string>? KeyOf { get; set; }

    internal Func<string, GoldpathCampaignItemContext, CancellationToken, Task<GoldpathCampaignActionResult>> ExecuteItem { get; set; } = null!;

    internal Func<object, string> SerializeTarget { get; set; } = null!;
}

/// <summary>Fluent registration surface for one campaign type.</summary>
public sealed class GoldpathCampaignTypeBuilder<TTarget>
    where TTarget : class
{
    private readonly GoldpathCampaignType _type;
    private Func<IServiceProvider, IReadOnlyDictionary<string, string>, IAsyncEnumerable<TTarget>>? _targets;
    private Func<IServiceProvider, IReadOnlyDictionary<string, string>, string?, IAsyncEnumerable<TTarget>>? _targetsAfter;
    private Func<TTarget, string>? _keyOf;

    internal GoldpathCampaignTypeBuilder(GoldpathCampaignType type) => _type = type;

    /// <summary>Sets the mandatory enumeration ceiling.</summary>
    public GoldpathCampaignTypeBuilder<TTarget> MaxTargets(long maxTargets)
    {
        if (maxTargets <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTargets), "The target ceiling must be positive.");
        }

        _type.MaxTargets = maxTargets;
        return this;
    }

    /// <summary>
    /// Binds the streaming target selector. Resolve YOUR DbContext from the provider and
    /// return a keyset-ORDERED stream — the enumerator checkpoints by count, so the order
    /// must be stable across a leader takeover.
    /// </summary>
    public GoldpathCampaignTypeBuilder<TTarget> Targets(
        Func<IServiceProvider, IReadOnlyDictionary<string, string>, IAsyncEnumerable<TTarget>> targets)
    {
        _targets = targets;
        return this;
    }

    /// <summary>
    /// Binds a KEYSET selector (R2.7): the third argument is the key of the last target
    /// already materialized (null on a fresh start), and the stream must begin strictly
    /// AFTER it in a stable order; <paramref name="keyOf"/> names each target's key (at
    /// most 256 characters). A takeover then reopens the stream at the key instead of
    /// re-reading every materialized row to skip it — the difference between a seconds-long
    /// resume and a minutes-long one at tens of millions of targets.
    /// </summary>
    public GoldpathCampaignTypeBuilder<TTarget> TargetsAfter(
        Func<IServiceProvider, IReadOnlyDictionary<string, string>, string?, IAsyncEnumerable<TTarget>> targets,
        Func<TTarget, string> keyOf)
    {
        _targetsAfter = targets;
        _keyOf = keyOf;
        return this;
    }

    /// <summary>Sets the per-type release ceiling shared by every campaign of this type (R2.4).</summary>
    public GoldpathCampaignTypeBuilder<TTarget> MaxTps(int maxTps)
    {
        if (maxTps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTps), "The type ceiling must be positive.");
        }

        _type.MaxTps = maxTps;
        return this;
    }

    /// <summary>Sets the default policy new instances start from.</summary>
    public GoldpathCampaignTypeBuilder<TTarget> DefaultPolicy(Func<GoldpathCampaignPolicy, GoldpathCampaignPolicy> configure)
    {
        _type.DefaultPolicy = configure(_type.DefaultPolicy);
        return this;
    }

    internal void Bake()
    {
        if (_type.MaxTargets == 0)
        {
            throw new InvalidOperationException(
                $"Campaign type '{_type.Key}' has no MaxTargets — an unbounded enumeration at L4 scale is an outage, not a campaign (campaign RFC D7 / GP1701).");
        }

        if (_targets is not null && _targetsAfter is not null)
        {
            throw new InvalidOperationException(
                $"Campaign type '{_type.Key}' binds both Targets and TargetsAfter — pick one: a type resumes by count or by key, not both.");
        }

        if (_targetsAfter is { } keyed && _keyOf is { } keyOf)
        {
            _type.Enumerate = (services, parameters) => Upcast(keyed(services, parameters, null));
            _type.EnumerateAfter = (services, parameters, after) => Upcast(keyed(services, parameters, after));
            _type.KeyOf = target => keyOf((TTarget)target);
        }
        else
        {
            var targets = _targets ?? throw new InvalidOperationException(
                $"Campaign type '{_type.Key}' has no Targets selector — a campaign that cannot enumerate is a typo.");
            _type.Enumerate = (services, parameters) => Upcast(targets(services, parameters));
        }

        _type.SerializeTarget = target => JsonSerializer.Serialize((TTarget)target);
        _type.ExecuteItem = async (targetJson, context, cancellationToken) =>
        {
            var target = JsonSerializer.Deserialize<TTarget>(targetJson)
                ?? throw new InvalidOperationException($"Campaign item payload of '{_type.Key}' deserialized to null.");
            // R2.1: the typed-result handler wins when registered; the R1 handler keeps its
            // contract (return = success, throw = retryable failure) so no adopter recompiles.
            if (context.Services.GetService<IGoldpathCampaignActionHandler<TTarget>>() is { } action)
            {
                return await action.ExecuteAsync(target, context, cancellationToken)
                    ?? throw new InvalidOperationException($"The action handler of '{_type.Key}' returned null — answer Succeeded, Failed or Accepted.");
            }

            var handler = context.Services.GetService<IGoldpathCampaignItemHandler<TTarget>>()
                ?? throw new InvalidOperationException(
                    $"No IGoldpathCampaignActionHandler<{typeof(TTarget).Name}> or IGoldpathCampaignItemHandler<{typeof(TTarget).Name}> is registered for campaign type '{_type.Key}'.");
            await handler.ExecuteAsync(target, context, cancellationToken);
            return GoldpathCampaignActionResult.Succeeded();
        };
    }

    private static async IAsyncEnumerable<object> Upcast(IAsyncEnumerable<TTarget> source)
    {
        await foreach (var item in source)
        {
            yield return item;
        }
    }
}

/// <summary>
/// Campaign composition options (campaign RFC §4). Types bake their closures at
/// registration; the engine, the pacer and the consumers stay non-generic.
/// </summary>
public sealed class GoldpathCampaignOptions
{
    internal Dictionary<string, GoldpathCampaignType> TypeMap { get; } = new(StringComparer.Ordinal);

    /// <summary>The registered campaign types.</summary>
    public IReadOnlyCollection<GoldpathCampaignType> Types => TypeMap.Values;

    /// <summary>Items materialized per enumeration step (each step is one durable write).</summary>
    public int EnumerationBatchSize { get; set; } = 2_000;

    /// <summary>Items released per broker publish batch (bounded so a tick stays cheap).</summary>
    public int ReleaseBatchSize { get; set; } = 500;

    /// <summary>The leader's in-memory tick (constraint 1: local ticks, not cluster locks).</summary>
    public TimeSpan LeaderTick { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long one pacer RUN leads before returning (the cron re-fires it).</summary>
    public TimeSpan LeadershipSlice { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>A Processing claim older than this is an interrupted consumer (sweep to repair).</summary>
    public TimeSpan StaleClaimAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A Released item still unclaimed after this long is an orphan (R2.6): its message
    /// was dropped — the campaign was paused while it sat in the queue, the broker lost
    /// it, a consumer refused the claim — and the pacer publishes it again under the same
    /// allowance. A resume marks every Released item due at once, so this is the ceiling
    /// on how long a lost message waits, not the usual wait.
    /// </summary>
    public TimeSpan OrphanReleaseAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-process release ceiling across ALL running campaigns (R1.4); null = off.
    /// PLATFORM protection, not provider fairness: the pacer is a cluster singleton per
    /// app, so per-process IS per-app. Cross-app limiting belongs at the gateway.
    /// </summary>
    public int? GlobalTps { get; set; }

    /// <summary>
    /// The retry ladder (R2.3): the wait before attempt N+1 is rung N, the last rung
    /// repeating for any further attempts. Default 30s, 2m, 10m — the R1 pair plus a rung
    /// that lets a target system finish a maintenance window before the item is given up.
    /// </summary>
    public IReadOnlyList<TimeSpan> RetryBackoff { get; set; }
        = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    /// <summary>
    /// Fractional jitter on every rung (R2.3), ±: 0.2 spreads a 30s rung over 24–36s so a
    /// thousand items that failed in the same second do not come back in the same second.
    /// 0 pins the exact ladder (tests that count seconds use it).
    /// </summary>
    public double RetryJitter { get; set; } = 0.2;

    /// <summary>The wait before attempt N+1 given N attempts so far, jittered per call.</summary>
    public TimeSpan NextRetryDelay(int attemptsSoFar)
    {
        if (RetryBackoff.Count == 0)
        {
            throw new InvalidOperationException("RetryBackoff needs at least one rung.");
        }

        var rung = RetryBackoff[Math.Clamp(attemptsSoFar - 1, 0, RetryBackoff.Count - 1)];
        if (RetryJitter <= 0)
        {
            return rung;
        }

        var factor = 1 + ((Random.Shared.NextDouble() * 2) - 1) * Math.Min(RetryJitter, 1);
        return TimeSpan.FromTicks((long)(rung.Ticks * factor));
    }

    /// <summary>Registers one campaign type.</summary>
    public GoldpathCampaignOptions AddCampaign<TTarget>(string key, Action<GoldpathCampaignTypeBuilder<TTarget>> configure)
        where TTarget : class
    {
        if (TypeMap.ContainsKey(key))
        {
            throw new InvalidOperationException($"Campaign type '{key}' is already registered.");
        }

        var type = new GoldpathCampaignType(key);
        var builder = new GoldpathCampaignTypeBuilder<TTarget>(type);
        configure(builder);
        builder.Bake();
        TypeMap[key] = type;
        return this;
    }

    /// <summary>Finds a type or fails with a teaching message.</summary>
    public GoldpathCampaignType Type(string key)
        => TypeMap.TryGetValue(key, out var type)
            ? type
            : throw new InvalidOperationException(
                $"No campaign type named '{key}' — registered: {string.Join(", ", TypeMap.Keys)}.");
}
