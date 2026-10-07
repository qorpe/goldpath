using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The PACER — a LONG-LIVED leader run (campaign RFC D2, constraint 1): the ~1-minute
/// cron only guarantees a leader EXISTS; once running, this run loops in-memory at
/// <see cref="GoldpathCampaignOptions.LeaderTick"/> for one leadership slice, enumerating
/// ahead, releasing under policy (window/quota/TPS/in-flight), sweeping stale claims,
/// and completing campaigns. Death → the next fire takes over from the durable
/// watermarks (counter warmup = reading the row — the D3 reconcile).
/// </summary>
public sealed class GoldpathCampaignPacerJob<TContext> : IGoldpathJob, IGoldpathItemReplay
    where TContext : DbContext
{
    private readonly GoldpathCampaignEngine<TContext> _engine;
    private readonly GoldpathCampaignOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathCampaignPacerJob<TContext>> _logger;

    /// <summary>Resolved per fire.</summary>
    public GoldpathCampaignPacerJob(
        GoldpathCampaignEngine<TContext> engine, GoldpathCampaignOptions options,
        TimeProvider time, ILogger<GoldpathCampaignPacerJob<TContext>> logger)
    {
        _engine = engine;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<GoldpathJobPlan> PlanAsync(GoldpathJobContext context, CancellationToken cancellationToken)
        => Task.FromResult(new GoldpathJobPlan(["lead"]));   // one chunk = one leadership slice

    /// <inheritdoc />
    public async Task ExecuteChunkAsync(GoldpathJobChunk chunk, GoldpathJobContext context, CancellationToken cancellationToken)
    {
        var sliceEnd = _time.GetUtcNow() + _options.LeadershipSlice;
        // Leader-local state (D3's cache tier at single-leader): fractional TPS budgets
        // and open enumeration streams live HERE; the durable rows stay the truth.
        var tpsBudgets = new Dictionary<Guid, double>();
        // R1.4: ONE bucket over every campaign in the slice — per-campaign limits cannot
        // see each other, and five campaigns at once must not melt the platform.
        var globalBudget = 0d;
        // R2.4: one bucket per campaign TYPE that declares a ceiling — the target system's rate.
        var typeBudgets = new Dictionary<string, double>(StringComparer.Ordinal);
        var streams = new Dictionary<Guid, LeaderStream>();
        var tick = 0;
        try
        {
            while (_time.GetUtcNow() < sliceEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tickStart = _time.GetTimestamp();
                tick++;
                // Each tick gets its OWN scope: the runner tracks its checkpoint entities
                // in the fire's scope, and the engine's tracker hygiene must never detach
                // them (a cleared checkpoint = a run that resumes forever).
                using var tickScope = context.Services.CreateScope();
                var db = tickScope.ServiceProvider.GetRequiredService<TContext>();
                var campaigns = await _engine.LoadWorkableAsync(db, cancellationToken);
                db.ChangeTracker.Clear();
                if (_options.GlobalTps is { } globalTps)
                {
                    globalBudget = Math.Min(globalBudget + globalTps * _options.LeaderTick.TotalSeconds, globalTps);
                }

                foreach (var type in _options.Types)
                {
                    if (type.MaxTps is { } maxTps)
                    {
                        typeBudgets[type.Key] = Math.Min(
                            typeBudgets.GetValueOrDefault(type.Key) + maxTps * _options.LeaderTick.TotalSeconds, maxTps);
                    }
                }

                // Phase 1 — each campaign's OWN allowance this tick (policy, window, quota, in-flight).
                var demands = new int[campaigns.Count];
                for (var i = 0; i < campaigns.Count; i++)
                {
                    demands[i] = await PrepareTickAsync(tickScope.ServiceProvider, campaigns[i], tpsBudgets, streams, cancellationToken);
                }

                // Phase 2 — the shared ceilings, split by weighted fair share (R2.4 + R2.5).
                var grants = Allocate(campaigns, demands, typeBudgets, _options.GlobalTps is null ? null : globalBudget, tick);

                // Phase 3 — release under the grant, then the sweeps and the completion math.
                for (var i = 0; i < campaigns.Count; i++)
                {
                    if (demands[i] < 0)
                    {
                        continue;   // not releasing this tick (paused, enumerating, closed window, expired)
                    }

                    var spent = await ReleaseTickAsync(tickScope.ServiceProvider, campaigns[i], grants[i], tpsBudgets, chunk, cancellationToken);
                    globalBudget -= spent;
                    if (typeBudgets.ContainsKey(campaigns[i].Type))
                    {
                        typeBudgets[campaigns[i].Type] -= spent;
                    }
                }

                var elapsed = _time.GetElapsedTime(tickStart);
                if (elapsed < _options.LeaderTick)
                {
                    await Task.Delay(_options.LeaderTick - elapsed, _time, cancellationToken);
                }
            }
        }
        finally
        {
            foreach (var stream in streams.Values)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Caps each campaign's own allowance by the shared ceilings: first the type bucket
    /// among the campaigns of that type, then the global bucket among everyone — each a
    /// weighted max-min split, so a High campaign cannot be starved by a Low one that
    /// happened to be created first (R2.5).
    /// </summary>
    private static int[] Allocate(List<GoldpathCampaign> campaigns, int[] demands, Dictionary<string, double> typeBudgets, double? globalBudget, int tick)
    {
        var capped = new int[campaigns.Count];
        for (var i = 0; i < campaigns.Count; i++)
        {
            capped[i] = Math.Max(0, demands[i]);
        }

        foreach (var group in campaigns.Select((campaign, index) => (campaign, index)).GroupBy(x => x.campaign.Type, StringComparer.Ordinal))
        {
            if (!typeBudgets.TryGetValue(group.Key, out var budget))
            {
                continue;
            }

            var members = group.ToArray();
            var claims = members
                .Select(m => new GoldpathCampaignFairShareClaim(GoldpathCampaignFairShare.WeightOf(m.campaign.Priority), capped[m.index]))
                .ToArray();
            var granted = GoldpathCampaignFairShare.Allocate(claims, (int)Math.Floor(Math.Max(0, budget)), tick % Math.Max(1, members.Length));
            for (var k = 0; k < members.Length; k++)
            {
                capped[members[k].index] = granted[k];
            }
        }

        if (globalBudget is { } shared)
        {
            var claims = campaigns
                .Select((campaign, index) => new GoldpathCampaignFairShareClaim(GoldpathCampaignFairShare.WeightOf(campaign.Priority), capped[index]))
                .ToArray();
            capped = GoldpathCampaignFairShare.Allocate(claims, (int)Math.Floor(Math.Max(0, shared)), tick % Math.Max(1, campaigns.Count));
        }

        return capped;
    }

    /// <summary>
    /// One open target stream + the scope it reads through. The stream MUST NOT share the
    /// tick's DbContext: enumeration keeps a data reader open across ticks, and the item
    /// writes would collide with it on the same connection.
    /// </summary>
    private sealed class LeaderStream(IServiceScope scope, IAsyncEnumerator<object> stream) : IAsyncDisposable
    {
        public IAsyncEnumerator<object> Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            scope.Dispose();
        }
    }

    /// <summary>
    /// Phase 1 of a tick: expiry, enumeration, and the campaign's OWN allowance from the
    /// live policy. Returns -1 when the campaign releases nothing this tick (and needs no
    /// sweep), otherwise the allowance before the shared ceilings cap it.
    /// </summary>
    private async Task<int> PrepareTickAsync(
        IServiceProvider services, GoldpathCampaign campaign,
        Dictionary<Guid, double> tpsBudgets, Dictionary<Guid, LeaderStream> streams, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();

        // R1.2 first: past the end date the remainder is a REPORT — a campaign born
        // expired must not even enumerate, and the flip must not wait out a slice
        // boundary behind enumeration.
        if (campaign.State is GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating or GoldpathCampaignState.Running
            && GoldpathCampaignEngine<TContext>.PolicyOf(campaign).IsExpired(_time.GetUtcNow()))
        {
            if (await TryExpireAsync(db, campaign, cancellationToken) && streams.Remove(campaign.Id, out var open))
            {
                await open.DisposeAsync();
            }

            return -1;
        }

        // 1) Enumerate ahead (streaming, ceilinged) while the source has more.
        if (!campaign.EnumerationComplete && campaign.State is GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating or GoldpathCampaignState.Running)
        {
            if (!streams.TryGetValue(campaign.Id, out var stream))
            {
                var streamScope = services.CreateScope();
                try
                {
                    stream = new LeaderStream(streamScope,
                        await _engine.OpenStreamAtWatermarkAsync(streamScope.ServiceProvider, campaign, cancellationToken));
                }
                catch
                {
                    streamScope.Dispose();
                    throw;
                }

                streams[campaign.Id] = stream;
            }

            await _engine.EnumerateStepAsync(services, campaign, stream.Stream, cancellationToken);
            if (campaign.EnumerationComplete && streams.Remove(campaign.Id, out var done))
            {
                await done.DisposeAsync();
            }
        }

        if (campaign.State != GoldpathCampaignState.Running)
        {
            return -1;   // paused / ceiling-paused / still enumerating first batch
        }

        // 2) Policy math on the LIVE row values (throttle takes effect within one tick).
        var policy = GoldpathCampaignEngine<TContext>.PolicyOf(campaign);
        var now = _time.GetUtcNow();

        await _engine.RollQuotaDayIfNeededAsync(db, campaign, cancellationToken);
        if (!policy.IsDayAllowed(now))
        {
            // R1.1: an excluded day releases nothing — and the day AFTER resumes
            // without a human. In-flight items still drain; the sink still applies.
            GoldpathCampaignMetrics.WindowClosed(campaign.Type);
            return -1;
        }

        if (!policy.IsWindowOpen(now))
        {
            GoldpathCampaignMetrics.WindowClosed(campaign.Type);
            return -1;
        }

        var budget = tpsBudgets.GetValueOrDefault(campaign.Id)
            + policy.Tps * _options.LeaderTick.TotalSeconds;
        budget = Math.Min(budget, policy.Tps);   // never bank more than one second of tokens
        tpsBudgets[campaign.Id] = budget;
        var inFlight = campaign.ReleasedThrough - campaign.SucceededCount - campaign.FailedCount;
        return (int)Math.Floor(Math.Min(budget, Math.Max(0,
            Math.Min(policy.MaxInFlight - inFlight,
                policy.DailyQuota is { } quota ? quota - campaign.ReleasedToday : long.MaxValue))));
    }

    /// <summary>
    /// Phase 3 of a tick: release under the granted allowance (ripe retries first, then
    /// orphans, then fresh items — all three ride the same grant), then the sweeps and
    /// the completion math. Returns what was spent.
    /// </summary>
    private async Task<int> ReleaseTickAsync(
        IServiceProvider services, GoldpathCampaign campaign, int grant,
        Dictionary<Guid, double> tpsBudgets, GoldpathJobChunk chunk, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var inFlight = campaign.ReleasedThrough - campaign.SucceededCount - campaign.FailedCount;

        var spent = 0;
        if (grant > 0)
        {
            // R1.3: ripe retries ride the SAME allowance as fresh releases — a retry storm
            // must not out-run the policy any more than a first send may. R2.6: so do
            // orphaned releases — a re-publish is a send.
            var retried = await _engine.ReleaseRipeRetriesAsync(services, campaign, grant, cancellationToken);
            var orphans = retried >= grant
                ? 0
                : await _engine.ReleaseOrphansAsync(services, campaign, grant - retried, cancellationToken);
            var released = retried + orphans >= grant
                ? 0
                : await _engine.ReleaseBatchAsync(services, campaign, grant - retried - orphans, cancellationToken);
            spent = retried + orphans + released;
            tpsBudgets[campaign.Id] = tpsBudgets.GetValueOrDefault(campaign.Id) - spent;
        }

        // 3a) Ack sweep (R2.1): accepted items whose callback never came walk the ladder.
        await _engine.SweepAckTimeoutsAsync(db, campaign, cancellationToken);

        // 3) Stale-claim sweep: a consumer died between claim and outcome — repair, never resend.
        var staleBefore = now - _options.StaleClaimAfter;
        var stale = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.Processing && i.ClaimedAt < staleBefore)
            .Select(i => i.Seq)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (stale.Count > 0)
        {
            await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaign.Id && stale.Contains(i.Seq) && i.State == GoldpathCampaignItemState.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.State, GoldpathCampaignItemState.Failed)
                    .SetProperty(i => i.CompletedAt, now)
                    .SetProperty(i => i.Error, "interrupted mid-flight — confirm the provider, then replay"), cancellationToken);
            await db.Set<GoldpathCampaign>().Where(c => c.Id == campaign.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedCount, c => c.FailedCount + stale.Count), cancellationToken);
            foreach (var seq in stale)
            {
                chunk.ReportItemFailure(
                    string.Create(CultureInfo.InvariantCulture, $"{campaign.Id:N}#{seq}"),
                    "interrupted mid-flight — confirm the provider, then replay");
            }

            _logger.LogWarning("Campaign {CampaignId}: {Count} stale claims swept to the repair queue.", campaign.Id, stale.Count);
        }

        // 4) Completion math (fresh row — the sink may have flushed since we loaded).
        // On the FAILURE flip (token-guarded, so it fires exactly once) the whole failed
        // set files into the repair queue — the jobs replay-items verb is the heal path.
        // Stale-swept items were already filed by an earlier slice; a second entry under
        // this run is harmless (replay of an already-healed item is a no-op).
        if (await _engine.TryCompleteAsync(db, campaign.Id, cancellationToken) == GoldpathCampaignState.CompletedWithFailures)
        {
            var failedItems = await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.Failed)
                .Select(i => new { i.Seq, i.Error })
                .ToListAsync(cancellationToken);
            foreach (var failed in failedItems)
            {
                chunk.ReportItemFailure(
                    string.Create(CultureInfo.InvariantCulture, $"{campaign.Id:N}#{failed.Seq}"),
                    failed.Error ?? "failed");
            }
        }

        db.ChangeTracker.Clear();
        GoldpathCampaignMetrics.Snapshot(campaign, inFlight);
        return spent;
    }

    /// <summary>
    /// Flips a Running campaign whose EndDate passed to ExpiredIncomplete (state-token
    /// guarded, so a racing verb wins and the flip re-evaluates next tick). The remaining
    /// count IS the report; nothing is stamped onto the items themselves.
    /// </summary>
    private async Task<bool> TryExpireAsync(TContext db, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        try
        {
            db.Attach(campaign);
            campaign.State = GoldpathCampaignState.ExpiredIncomplete;
            campaign.CompletedAt = _time.GetUtcNow();
            campaign.LastVerb = $"goldpath: end date {campaign.EndDate:yyyy-MM-dd} passed with "
                + $"{campaign.EnumeratedThrough - campaign.SucceededCount - campaign.FailedCount} items remaining — the remainder is a report";
            await db.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Campaign {CampaignId} expired incomplete at its end date.", campaign.Id);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;   // a verb raced us; next tick re-evaluates
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <inheritdoc />
    public Task ReplayItemAsync(string itemKey, GoldpathJobContext context, CancellationToken cancellationToken)
        => _engine.ReplayItemAsync(context.Services, itemKey, cancellationToken);
}
