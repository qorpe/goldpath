using System.Diagnostics.Metrics;

namespace Goldpath;

/// <summary>
/// The campaign meter (`Goldpath.Campaign`, RFC §7): the operator's governor — released rate
/// vs ceiling, outcome totals, in-flight, window state. Run progress rides the JOBS
/// meter; the per-campaign panel is S2's board.
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

    private static readonly object SnapshotLock = new();
    private static readonly Dictionary<Guid, (string Type, long Remaining, long InFlight)> Snapshots = [];

    static GoldpathCampaignMetrics()
    {
        Meter.CreateObservableGauge("goldpath_campaign_in_flight", ObserveInFlight,
            description: "Released-but-not-terminal items per campaign (the MaxInFlight governor's view).");
        Meter.CreateObservableGauge("goldpath_campaign_remaining", ObserveRemaining,
            description: "Items not yet released per campaign (progress/ETA math).");
    }

    internal static void Released(string type, int count)
        => ReleasedTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void Orphans(string type, int count)
        => OrphansTotal.Add(count, new KeyValuePair<string, object?>("type", type));

    internal static void AckTimeouts(string type, int count)
        => AckTimeoutsTotal.Add(count, new KeyValuePair<string, object?>("type", type));

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
}
