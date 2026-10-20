namespace Goldpath;

/// <summary>
/// One claimant on a shared budget (campaign RFC R2.5): how much weight its priority
/// carries and how much it could release this tick if nothing were shared.
/// </summary>
public readonly record struct GoldpathCampaignFairShareClaim(int Weight, int Demand);

/// <summary>
/// Weighted max-min fair share over a shared budget (campaign RFC R2.5). The platform ceiling
/// (GlobalTps) and a type's ceiling (MaxTps) are buckets that several campaigns drink from at
/// once; before R2 the first campaign in creation order drank first and a late, urgent
/// campaign starved behind a bulk one. Water-filling fixes that: a claimant never gets more
/// than it asked for, and what it leaves on the table is shared among the others by weight.
/// Units are whole, shares are not — so the fractional part is CARRIED from tick to tick as
/// a credit (R2 review #6): a Low campaign whose share is three quarters of a unit per tick
/// gets a unit three ticks in four, never zero forever. Over any run of ticks every hungry
/// claimant's total is its weighted share to within a unit.
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
    /// claim in the same order. Each claim's exact (fractional) weighted share is computed by
    /// water-filling; whole units then go, one at a time, to the hungry claimant furthest
    /// behind its share — its share this tick plus the <paramref name="credit"/> it carried in
    /// (heavier first on a tie, then by position counted from <paramref name="tieBreakStart"/>,
    /// which the pacer advances every tick). <paramref name="credit"/> is read and written in
    /// place, one slot per claim: pass the same values back next tick for the same claimants
    /// (null = a single tick with nothing carried). An idle claimant's credit resets — nothing
    /// is banked while not asking — and so does everyone's when the budget covers every demand.
    /// </summary>
    public static int[] Allocate(IReadOnlyList<GoldpathCampaignFairShareClaim> claims, int total, double[]? credit = null, int tieBreakStart = 0)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (credit is not null && credit.Length != claims.Count)
        {
            throw new ArgumentException("One credit slot per claim.", nameof(credit));
        }

        var count = claims.Count;
        var grants = new int[count];
        long demand = 0;
        for (var i = 0; i < count; i++)
        {
            if (claims[i].Demand > 0)
            {
                if (claims[i].Weight <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(claims), "Every claim needs a positive weight.");
                }

                demand += claims[i].Demand;
            }
            else if (credit is not null)
            {
                credit[i] = 0;   // idle: nothing is owed to a claimant that is not asking
            }
        }

        if (total <= 0 || demand == 0)
        {
            return grants;
        }

        if (demand <= total)
        {
            // No contention: everyone gets what it asked for, and nobody is owed anything.
            for (var i = 0; i < count; i++)
            {
                grants[i] = Math.Max(0, claims[i].Demand);
                if (credit is not null)
                {
                    credit[i] = 0;
                }
            }

            return grants;
        }

        var share = WaterFill(claims, total);
        var behind = new double[count];
        for (var i = 0; i < count; i++)
        {
            behind[i] = claims[i].Demand > 0 ? share[i] + (credit?[i] ?? 0) : double.NegativeInfinity;
        }

        for (var unit = 0; unit < total; unit++)
        {
            var pick = -1;
            for (var k = 0; k < count; k++)
            {
                var i = ((k + tieBreakStart) % count + count) % count;   // the rotation decides exact ties
                if (grants[i] >= claims[i].Demand)
                {
                    continue;
                }

                if (pick < 0
                    || behind[i] > behind[pick] + 1e-9
                    || (Math.Abs(behind[i] - behind[pick]) <= 1e-9 && claims[i].Weight > claims[pick].Weight))
                {
                    pick = i;
                }
            }

            grants[pick]++;
            behind[pick] -= 1;
        }

        if (credit is not null)
        {
            for (var i = 0; i < count; i++)
            {
                // What is carried is how far behind (or ahead of) its exact share each
                // claimant ended the tick; bounded so a pathological sequence cannot bank a burst.
                credit[i] = claims[i].Demand > 0 ? Math.Clamp(behind[i], -count, count) : 0;
            }
        }

        return grants;
    }

    /// <summary>
    /// The exact weighted max-min shares of <paramref name="total"/>: anyone whose whole demand
    /// fits inside its weighted share is settled at its demand, and what it did not need goes
    /// back into the pool for the others, round after round.
    /// </summary>
    private static double[] WaterFill(IReadOnlyList<GoldpathCampaignFairShareClaim> claims, int total)
    {
        var share = new double[claims.Count];
        var active = new List<int>(claims.Count);
        for (var i = 0; i < claims.Count; i++)
        {
            if (claims[i].Demand > 0)
            {
                active.Add(i);
            }
        }

        double remaining = total;
        while (active.Count > 0)
        {
            double weightSum = 0;
            foreach (var i in active)
            {
                weightSum += claims[i].Weight;
            }

            var settled = false;
            for (var k = active.Count - 1; k >= 0; k--)
            {
                var i = active[k];
                if (claims[i].Demand <= remaining * claims[i].Weight / weightSum)
                {
                    share[i] = claims[i].Demand;
                    remaining -= claims[i].Demand;
                    active.RemoveAt(k);
                    settled = true;
                }
            }

            if (!settled)
            {
                foreach (var i in active)
                {
                    share[i] = remaining * claims[i].Weight / weightSum;
                }

                break;
            }
        }

        return share;
    }
}
