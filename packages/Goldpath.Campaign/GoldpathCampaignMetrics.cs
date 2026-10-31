using System.Diagnostics.Metrics;

namespace Goldpath;

/// <summary>
/// The campaign meter (`Goldpath.Campaign`, RFC §7): the operator's governor — released rate
/// vs ceiling, outcome totals and their error codes, in-flight, the limit that binds, the
/// handler's latency and how far the sink's durable truth trails it. Run progress rides the
/// JOBS meter; the per-campaign panel is S2's board. RFC §7 lists every instrument, and
/// <c>MetricsExportTests</c> holds each one's name, unit and tags.
/// </summary>
public static class GoldpathCampaignMetrics
{
    private static readonly Meter Meter = new("Goldpath.Campaign");

    private static readonly Counter<long> ReleasedTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_released_total", description: "Items released to the broker, per campaign type.");

    private static readonly Counter<long> SucceededTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_succeeded_total", description: "Items whose handler succeeded (sink-batched).");

    private static readonly Counter<long> FailedTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_failed_total", description: "Items whose handler failed or whose claim went stale.");

    private static readonly Counter<long> WindowClosedTicks = Meter.CreateCounter<long>(
        "goldpath_campaign_window_closed_ticks_total", description: "Leader ticks skipped because the send window was closed.");

    private static readonly Counter<long> AckTimeoutsTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_ack_timeouts_total", description: "Accepted items whose callback never came before the deadline (R2.1).");

    private static readonly Counter<long> OrphansTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_orphans_rereleased_total", description: "Released items published again because no consumer claimed them in time (R2.6).");

    private static readonly Counter<long> CallbacksParkedTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_callbacks_parked_total", description: "Callbacks that arrived before their acceptance landed and were parked (R2.1).");

    private static readonly Counter<long> CallbacksRejectedTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_callbacks_rejected_total", description: "Callbacks refused for a missing, stale or wrong signature (R2.8).");

    private static readonly Counter<long> OutcomesTotal = Meter.CreateCounter<long>(
        "goldpath_campaign_outcomes_total",
        description: "Outcomes the sink applied, per campaign type, outcome (succeeded, accepted, retry, failed) and the provider's error code (R2.1).");

    private static readonly Histogram<double> HandlerDuration = Meter.CreateHistogram<double>(
        "goldpath_campaign_handler_duration", unit: "s",
        description: "One item handler call (the adapter's latency), per campaign type; a throw is timed too.");

    private static readonly Histogram<double> OutcomeLag = Meter.CreateHistogram<double>(
        "goldpath_campaign_sink_flush_lag", unit: "s",
        description: "From an attempt's claim to the sink flush that made its outcome durable, per campaign type: the handler's time plus the sink's lag.");

    /// <summary>The most distinct error codes one type reports before the rest read <see cref="OtherErrorCode"/>.</summary>
    internal const int MaxErrorCodesPerType = 32;

    /// <summary>The longest error code reported as itself; a longer one reads <see cref="OtherErrorCode"/>.</summary>
    internal const int MaxErrorCodeLength = 64;

    /// <summary>The error-code tag of a failure whose code is over the cap or too long.</summary>
    internal const string OtherErrorCode = "other";

    /// <summary>The error-code tag of an outcome that carries none (every success, a failure without a code).</summary>
    internal const string NoErrorCode = "none";

    private static readonly object SnapshotLock = new();
    private static readonly Dictionary<Guid, (string Type, long Remaining, long InFlight)> Snapshots = [];
    private static readonly Dictionary<string, HashSet<string>> ErrorCodes = new(StringComparer.Ordinal);
    private static readonly Dictionary<object, GoldpathCampaignTickState> Ticks = new(ReferenceEqualityComparer.Instance);

    static GoldpathCampaignMetrics()
    {
        Meter.CreateObservableGauge("goldpath_campaign_in_flight", ObserveInFlight,
            description: "Released-but-not-terminal items per campaign (the MaxInFlight governor's view).");
        Meter.CreateObservableGauge("goldpath_campaign_remaining", ObserveRemaining,
            description: "Items not yet released per campaign (progress/ETA math).");
        Meter.CreateObservableGauge("goldpath_campaign_tps_ceiling", ObserveTpsCeiling,
            description: "The Tps each running campaign's policy allows (the ceiling its released rate is read against).");
        Meter.CreateObservableGauge("goldpath_campaign_type_max_tps", ObserveTypeCeilings,
            description: "Each campaign type's MaxTps, the shared ceiling of its target system (R2.4); types without one are absent.");
        Meter.CreateObservableGauge("goldpath_campaign_global_tps", ObserveGlobalTps,
            description: "GlobalTps, the one ceiling over every campaign (R1.4); absent when not configured.");
        Meter.CreateObservableGauge("goldpath_campaign_binding_constraint", ObserveBinding,
            description: "1 for the limit that bound each running campaign's last tick: tps, in_flight, quota, window, type, global or none.");
        Meter.CreateObservableGauge("goldpath_campaign_released_today", ObserveReleasedToday,
            description: "Items each running campaign released in its quota day (DailyQuota consumption).");
        Meter.CreateObservableGauge("goldpath_campaign_daily_quota", ObserveDailyQuota,
            description: "Each running campaign's DailyQuota; campaigns without one are absent.");
        Meter.CreateObservableGauge("goldpath_campaign_leader_last_tick", ObserveLastTick, unit: "s",
            description: "When this process's pacer last ticked, in Unix seconds; its age is the leader's liveness.");
    }

    internal static void Released(string type, int count)
        => ReleasedTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void Orphans(string type, int count)
        => OrphansTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void AckTimeouts(string type, int count)
        => AckTimeoutsTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void CallbacksParked(string type, int count)
        => CallbacksParkedTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void CallbacksRejected(string type)
        => CallbacksRejectedTotal.Add(1, new KeyValuePair<string, object?>("type", type));

    internal static void Outcomes(Guid campaignId, int succeeded, int failed)
    {
        var tag = new KeyValuePair<string, object?>("campaign", campaignId.ToString("N"));
        if (succeeded > 0)
        {
            SucceededTotal.Add(succeeded, tag);
        }

        if (failed > 0)
        {
            FailedTotal.Add(failed, tag);
        }
    }

    /// <summary>
    /// What the sink applied, counted by the rows it moved: <paramref name="outcome"/> is
    /// <c>succeeded</c>, <c>accepted</c>, <c>retry</c> or <c>failed</c>.
    /// </summary>
    internal static void Applied(string type, string outcome, string? errorCode, int count)
    {
        if (count <= 0)
        {
            return;
        }

        OutcomesTotal.Add(count,
            new KeyValuePair<string, object?>("type", type),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("error_code", ErrorCodeTag(type, errorCode)));
    }

    /// <summary>
    /// The error-code tag, its cardinality bounded: the first <see cref="MaxErrorCodesPerType"/>
    /// distinct codes a type reports are themselves, every later one and any code longer than
    /// <see cref="MaxErrorCodeLength"/> reads <see cref="OtherErrorCode"/>. A code comes from a
    /// provider, so an unbounded tag would let one misbehaving target system grow every scrape.
    /// </summary>
    internal static string ErrorCodeTag(string type, string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return NoErrorCode;
        }

        if (errorCode.Length > MaxErrorCodeLength)
        {
            return OtherErrorCode;
        }

        lock (SnapshotLock)
        {
            if (!ErrorCodes.TryGetValue(type, out var known))
            {
                known = new HashSet<string>(StringComparer.Ordinal);
                ErrorCodes[type] = known;
            }

            return known.Contains(errorCode) || (known.Count < MaxErrorCodesPerType && known.Add(errorCode))
                ? errorCode
                : OtherErrorCode;
        }
    }

    /// <summary>One item handler call's duration.</summary>
    internal static void HandlerCall(string type, TimeSpan elapsed)
        => HandlerDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("type", type));

    /// <summary>How long after its claim an outcome became durable.</summary>
    internal static void Flushed(string type, TimeSpan sinceClaim)
        => OutcomeLag.Record(Math.Max(0, sinceClaim.TotalSeconds), new KeyValuePair<string, object?>("type", type));

    /// <summary>
    /// A pacer's view at the end of a tick, replacing that pacer's previous one whole: the
    /// running campaigns it worked (one that stopped running leaves the gauges with the tick
    /// that no longer loaded it), the configured shared ceilings, and the tick's instant.
    /// <paramref name="owner"/> is the host's <see cref="GoldpathCampaignOptions"/>, so two
    /// campaign hosts in one process (a test run) never overwrite each other's view.
    /// </summary>
    internal static void Tick(object owner, GoldpathCampaignTickState state)
    {
        lock (SnapshotLock)
        {
            Ticks[owner] = state;
        }
    }

    internal static void WindowClosed(string type)
        => WindowClosedTicks.Add(1, new KeyValuePair<string, object?>("type", type));

    internal static void Snapshot(GoldpathCampaign campaign, long inFlight)
    {
        lock (SnapshotLock)
        {
            Snapshots[campaign.Id] = (campaign.Type,
                campaign.EnumeratedThrough - campaign.ReleasedThrough, inFlight);
        }
    }

    private static IEnumerable<Measurement<long>> ObserveInFlight()
    {
        List<Measurement<long>> measurements = [];
        lock (SnapshotLock)
        {
            foreach (var (id, snapshot) in Snapshots)
            {
                measurements.Add(new Measurement<long>(snapshot.InFlight,
                    new KeyValuePair<string, object?>("campaign", id.ToString("N")),
                    new KeyValuePair<string, object?>("type", snapshot.Type)));
            }
        }

        return measurements;
    }

    private static IEnumerable<Measurement<long>> ObserveRemaining()
    {
        List<Measurement<long>> measurements = [];
        lock (SnapshotLock)
        {
            foreach (var (id, snapshot) in Snapshots)
            {
                measurements.Add(new Measurement<long>(snapshot.Remaining,
                    new KeyValuePair<string, object?>("campaign", id.ToString("N")),
                    new KeyValuePair<string, object?>("type", snapshot.Type)));
            }
        }

        return measurements;
    }

    private static IEnumerable<Measurement<long>> ObserveTpsCeiling()
        => PerCampaign(facts => facts.Tps);

    private static IEnumerable<Measurement<long>> ObserveReleasedToday()
        => PerCampaign(facts => facts.ReleasedToday);

    private static IEnumerable<Measurement<long>> ObserveDailyQuota()
        => PerCampaign(facts => facts.DailyQuota);

    private static IEnumerable<Measurement<long>> ObserveBinding()
    {
        List<Measurement<long>> measurements = [];
        lock (SnapshotLock)
        {
            foreach (var (id, facts) in Ticks.Values.SelectMany(t => t.Campaigns))
            {
                measurements.Add(new Measurement<long>(1,
                    new KeyValuePair<string, object?>("campaign", id.ToString("N")),
                    new KeyValuePair<string, object?>("type", facts.Type),
                    new KeyValuePair<string, object?>("constraint", facts.Binding)));
            }
        }

        return measurements;
    }

    private static IEnumerable<Measurement<long>> ObserveTypeCeilings()
    {
        List<Measurement<long>> measurements = [];
        lock (SnapshotLock)
        {
            foreach (var (type, ceiling) in Ticks.Values.SelectMany(t => t.TypeCeilings))
            {
                measurements.Add(new Measurement<long>(ceiling, new KeyValuePair<string, object?>("type", type)));
            }
        }

        return measurements;
    }

    private static IEnumerable<Measurement<long>> ObserveGlobalTps()
    {
        lock (SnapshotLock)
        {
            return [.. Ticks.Values.Where(t => t.GlobalTps is not null).Select(t => new Measurement<long>(t.GlobalTps!.Value))];
        }
    }

    private static IEnumerable<Measurement<double>> ObserveLastTick()
    {
        lock (SnapshotLock)
        {
            return [.. Ticks.Values.Select(t => new Measurement<double>(t.At.ToUnixTimeMilliseconds() / 1000d))];
        }
    }

    private static List<Measurement<long>> PerCampaign(Func<GoldpathCampaignTickFacts, long?> value)
    {
        List<Measurement<long>> measurements = [];
        lock (SnapshotLock)
        {
            foreach (var (id, facts) in Ticks.Values.SelectMany(t => t.Campaigns))
            {
                if (value(facts) is { } measured)
                {
                    measurements.Add(new Measurement<long>(measured,
                        new KeyValuePair<string, object?>("campaign", id.ToString("N")),
                        new KeyValuePair<string, object?>("type", facts.Type)));
                }
            }
        }

        return measurements;
    }
}

/// <summary>
/// One running campaign as the pacer's last tick saw it (RFC §7): its type, the policy's Tps
/// and DailyQuota, what it released in its quota day, and the limit that bound the tick.
/// </summary>
internal sealed record GoldpathCampaignTickFacts(string Type, int Tps, long? DailyQuota, long ReleasedToday, string Binding);

/// <summary>One pacer's last tick (RFC §7): the running campaigns, the configured shared ceilings and when it ran.</summary>
internal sealed record GoldpathCampaignTickState(
    IReadOnlyDictionary<Guid, GoldpathCampaignTickFacts> Campaigns, IReadOnlyDictionary<string, int> TypeCeilings,
    int? GlobalTps, DateTimeOffset At);
