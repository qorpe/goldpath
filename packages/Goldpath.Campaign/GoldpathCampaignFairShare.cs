namespace Goldpath;

/// <summary>
/// One claimant on a shared budget (campaign RFC R2.5): how much weight its priority
/// carries and how much it could release this tick if nothing were shared.
/// </summary>
public readonly record struct GoldpathCampaignFairShareClaim(int Weight, int Demand);

/// <summary>
/// Weighted max-min fair share over one tick's shared budget (campaign RFC R2.5). The
/// platform ceiling (GlobalTps) and a type's ceiling (MaxTps) are buckets that several
/// campaigns drink from at once; before R2 the first campaign in creation order drank
/// first and a late, urgent campaign starved behind a bulk one. Water-filling fixes that:
/// a claimant never gets more than it asked for, what it leaves on the table is shared
/// among the others by weight, and a hungry claimant is guaranteed its weighted share.
/// </summary>
public static class GoldpathCampaignFairShare
{
    /// <summary>The weight a priority carries: High 3, Normal 2, Low 1 — a 3:2:1 split under contention.</summary>
    public static int WeightOf(GoldpathCampaignPriority priority)
        => priority switch
        {
            GoldpathCampaignPriority.High => 3,
            GoldpathCampaignPriority.Low => 1,
            _ => 2,
        };

    /// <summary>
    /// Grants <paramref name="total"/> units across <paramref name="claims"/>, one grant per
    /// claim in the same order. Deterministic: rounding leftovers go to the heaviest claim
    /// first, then by position counted from <paramref name="tieBreakStart"/> — the pacer
    /// advances that start every tick, so a budget of one unit per tick rotates among
    /// equals instead of always landing on the first campaign created.
    /// </summary>
    public static int[] Allocate(IReadOnlyList<GoldpathCampaignFairShareClaim> claims, int total, int tieBreakStart = 0)
    {
        var grants = new int[claims.Count];
        if (total <= 0)
        {
            return grants;
        }

        var active = new List<int>(claims.Count);
        for (var i = 0; i < claims.Count; i++)
        {
            if (claims[i].Demand > 0)
            {
                if (claims[i].Weight <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(claims), "Every claim needs a positive weight.");
                }

                active.Add(i);
            }
        }

        var remaining = (long)total;
        while (active.Count > 0 && remaining > 0)
        {
            long weightSum = 0;
            foreach (var i in active)
            {
                weightSum += claims[i].Weight;
            }

            // Anyone whose whole demand fits inside its weighted share is settled now; the
            // share it did not need goes back into the pool for the next round.
            var settled = false;
            for (var k = active.Count - 1; k >= 0; k--)
            {
                var i = active[k];
                var share = remaining * claims[i].Weight / weightSum;
                if (claims[i].Demand <= share)
                {
                    grants[i] = claims[i].Demand;
                    remaining -= claims[i].Demand;
                    active.RemoveAt(k);
                    settled = true;
                }
            }

            if (settled)
            {
                continue;
            }

            // Everyone left wants more than its share: hand out the floors, then the
            // rounding remainder one unit at a time, heaviest first, position as the tie-break.
            long handed = 0;
            foreach (var i in active)
            {
                grants[i] = (int)(remaining * claims[i].Weight / weightSum);
                handed += grants[i];
            }

            var leftover = remaining - handed;
            var count = claims.Count;
            foreach (var i in active
                .OrderByDescending(i => claims[i].Weight)
                .ThenBy(i => ((i - tieBreakStart) % count + count) % count))
            {
                if (leftover <= 0)
                {
                    break;
                }

                if (grants[i] < claims[i].Demand)
                {
                    grants[i]++;
                    leftover--;
                }
            }

            break;
        }

        return grants;
    }
}
