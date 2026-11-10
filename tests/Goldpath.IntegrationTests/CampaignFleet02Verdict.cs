using System.Globalization;

namespace Goldpath.Tests.Integration;

/// <summary>What the S-FLEET-02 run counted once it stopped and drained.</summary>
/// <param name="Published">Item messages the bus published over the whole run.</param>
/// <param name="Stranded">Items still <c>Released</c> after the drain: published before the pause, never claimed, durable.</param>
/// <param name="Durable">Items in a terminal state (<c>Succeeded</c> or <c>Failed</c>).</param>
/// <param name="Sink">The campaign row's outcome counters (<c>SucceededCount + FailedCount</c>).</param>
/// <param name="ExecutedTargets">Distinct target ids the simulated target executed.</param>
/// <param name="ExecutedTwice">Target ids the simulated target executed more than once.</param>
internal sealed record Fleet02Counts(long Published, long Stranded, long Durable, long Sink, long ExecutedTargets, long ExecutedTwice);

/// <summary>The verdict of one S-FLEET-02 run against the thresholds the owner signed on #326.</summary>
internal sealed record Fleet02Result(
    string Label,
    double MeanTps,
    double TpsPerCore,
    double P95Deviation,
    long WithinBand,
    long Above,
    long Empty,
    long RateP5,
    long RateP50,
    long RateP95,
    long Lost,
    long Duplicated,
    bool MeetsMean,
    bool MeetsJitter,
    bool MeetsCeiling,
    bool ExactlyOnce,
    bool PassRequiresEveryWindowExact);

/// <summary>
/// S-FLEET-02's verdict, computed from the signed thresholds (#326, recorded in
/// <c>docs/strategy/scenario-campaign.md</c>, "S-FLEET-02's rig spec") and nothing else:
/// <list type="number">
/// <item>the mean release rate over the window is at least <c>floorTps</c> (2,000 as signed);</item>
/// <item>the p95 of the per-second rate's deviation from the configured rate is within ±5%
/// (nearest rank: at least 95% of one-second windows inside the band);</item>
/// <item>no one-second window releases more than the configured rate (#292's strict rule);</item>
/// <item>zero lost and zero duplicated.</item>
/// </list>
/// A run that holds 3 and 4 but misses 1 is labelled <c>HARDWARE CEILING</c> and still publishes
/// (signed point 4); a run that breaks 3 or 4 is a <c>FAIL</c> whatever its rate, because a rate over
/// lost, doubled or bursting work means nothing.
/// </summary>
internal static class CampaignFleet02Verdict
{
    /// <summary>The signed jitter band: ±5% of the configured rate.</summary>
    public const double Band = 0.05;

    /// <summary>The signed share of windows the band must hold: the p95.</summary>
    public const double Quantile = 0.95;

    /// <summary>Judges one run. <paramref name="perSecond"/> is the released count of each whole second of the measured window.</summary>
    public static Fleet02Result Judge(IReadOnlyList<long> perSecond, int configuredTps, int floorTps, int logicalCpus, Fleet02Counts counts)
    {
        ArgumentNullException.ThrowIfNull(perSecond);
        ArgumentNullException.ThrowIfNull(counts);
        if (perSecond.Count == 0)
        {
            throw new ArgumentException("The measured window holds no whole second.", nameof(perSecond));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(configuredTps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(floorTps);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalCpus);

        var mean = perSecond.Sum() / (double)perSecond.Count;
        var deviations = perSecond.Select(n => Math.Abs(n - configuredTps) / (double)configuredTps).Order().ToArray();
        var p95Deviation = Percentile(deviations, Quantile);
        var withinBand = perSecond.LongCount(n => Math.Abs(n - configuredTps) <= configuredTps * Band);
        var above = perSecond.LongCount(n => n > configuredTps);
        var empty = perSecond.LongCount(n => n == 0);
        var rates = perSecond.Order().ToArray();

        // Every publish is one distinct item that is now terminal or durably Released; more
        // publishes than that is a second release of an item. A terminal row the sink did not
        // count, or a target executed with no terminal row (or the reverse), is lost work.
        var duplicated = counts.ExecutedTwice + Math.Max(0, counts.Published - (counts.Durable + counts.Stranded));
        var lost = Math.Abs(counts.Durable - counts.Sink) + Math.Abs(counts.ExecutedTargets - counts.Durable)
            + Math.Max(0, counts.Durable + counts.Stranded - counts.Published);

        var meetsMean = mean >= floorTps;
        var meetsJitter = p95Deviation <= Band;
        var meetsCeiling = above == 0;
        var exactlyOnce = lost == 0 && duplicated == 0;
        var label = !exactlyOnce || !meetsCeiling ? "FAIL"
            : !meetsMean ? "HARDWARE CEILING"
            : !meetsJitter ? "FAIL"
            : "PASS";

        return new Fleet02Result(
            label, mean, mean / logicalCpus, p95Deviation, withinBand, above, empty,
            Percentile(rates, 0.05), Percentile(rates, 0.50), Percentile(rates, Quantile),
            lost, duplicated, meetsMean, meetsJitter, meetsCeiling, exactlyOnce,
            // Under a strict ceiling at the configured rate, a mean of at least the floor is only
            // reachable when the floor is below it — or when every second is exactly the rate.
            PassRequiresEveryWindowExact: floorTps >= configuredTps);
    }

    /// <summary>The nearest-rank percentile of an ascending array.</summary>
    public static T Percentile<T>(T[] sorted, double p)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>The per-second series as CSV: the second's UTC start and its released count.</summary>
    public static string ToCsv(IReadOnlyList<long> perSecond, DateTimeOffset from)
    {
        ArgumentNullException.ThrowIfNull(perSecond);
        var lines = new List<string>(perSecond.Count + 1) { "second_utc,released" };
        for (var i = 0; i < perSecond.Count; i++)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{from.AddSeconds(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{perSecond[i]}"));
        }

        return string.Join('\n', lines) + "\n";
    }
}
