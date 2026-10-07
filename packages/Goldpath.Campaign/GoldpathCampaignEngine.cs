using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The campaign engine (campaign RFC §2): instance creation, streaming enumeration under
/// the ceiling, policy-governed release, outcome application, completion math. The engine
/// owns STATE; leadership/scheduling belongs to the pacer run, delivery to MassTransit.
/// </summary>
public sealed class GoldpathCampaignEngine<TContext>
    where TContext : DbContext
{
    private readonly GoldpathCampaignOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<GoldpathCampaignEngine<TContext>> _logger;

    /// <summary>Creates the engine.</summary>
    public GoldpathCampaignEngine(GoldpathCampaignOptions options, TimeProvider time, ILogger<GoldpathCampaignEngine<TContext>> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>Creates a campaign INSTANCE over a registered type (operators launch, developers ship types — D1).</summary>
    public async Task<GoldpathCampaign> CreateAsync(
        IServiceProvider services, string typeKey, string name,
        IReadOnlyDictionary<string, string> parameters, GoldpathCampaignPolicy? policy,
        string? tenant, string actor, CancellationToken cancellationToken)
    {
        var type = _options.Type(typeKey);
        var effective = policy ?? type.DefaultPolicy;
        var db = services.GetRequiredService<TContext>();
        var campaign = new GoldpathCampaign
        {
            Id = Guid.NewGuid(),
            Type = type.Key,
            Name = name,
            State = GoldpathCampaignState.Created,
            ParametersJson = JsonSerializer.Serialize(parameters),
            Tps = effective.Tps,
            DailyQuota = effective.DailyQuota,
            MaxInFlight = effective.MaxInFlight,
            WindowStart = effective.WindowStart,
            WindowEnd = effective.WindowEnd,
            TimeZoneId = effective.TimeZoneId,
            ExcludedDays = effective.ExcludedDays.Count == 0 ? null : string.Join(",", effective.ExcludedDays),
            EndDate = effective.EndDate,
            MaxAttempts = effective.MaxAttempts,
            Priority = effective.Priority,
            QuotaDay = effective.LocalDay(_time.GetUtcNow()),
            CreatedAt = _time.GetUtcNow(),
            CreatedBy = actor,
            Tenant = tenant,
        };
        db.Set<GoldpathCampaign>().Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Campaign {CampaignId} ({Type}) created by {Actor}.", campaign.Id, type.Key, actor);
        return campaign;
    }

    /// <summary>The campaigns a leader slice works: Created (to start), Enumerating, Running.</summary>
    public async Task<List<GoldpathCampaign>> LoadWorkableAsync(TContext db, CancellationToken cancellationToken)
        => await db.Set<GoldpathCampaign>()
            .Where(c => c.State == GoldpathCampaignState.Created
                || c.State == GoldpathCampaignState.Enumerating
                || c.State == GoldpathCampaignState.Running)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>Reads the live policy snapshot off the row (every field is runtime-adjustable, D6).</summary>
    public static GoldpathCampaignPolicy PolicyOf(GoldpathCampaign campaign)
        => new(campaign.Tps, campaign.DailyQuota, campaign.MaxInFlight,
            campaign.WindowStart, campaign.WindowEnd, campaign.TimeZoneId)
        {
            ExcludedDays = GoldpathCampaignPolicy.ParseDays(campaign.ExcludedDays),
            EndDate = campaign.EndDate,
            MaxAttempts = campaign.MaxAttempts,
            Priority = campaign.Priority,
        };

    /// <summary>
    /// Materializes the next enumeration step (streaming, stable order, resumed BY COUNT
    /// after a takeover). A ceiling breach PAUSES the campaign with a teaching verb — at
    /// L4 scale that is an operator decision, not an automatic partial send.
    /// </summary>
    public async Task<int> EnumerateStepAsync(
        IServiceProvider services, GoldpathCampaign campaign, IAsyncEnumerator<object> stream,
        CancellationToken cancellationToken)
    {
        var type = _options.Type(campaign.Type);
        var db = services.GetRequiredService<TContext>();
        db.Attach(campaign);

        if (campaign.State == GoldpathCampaignState.Created)
        {
            campaign.State = GoldpathCampaignState.Enumerating;
            await db.SaveChangesAsync(cancellationToken);
        }

        var added = 0;
        while (added < _options.EnumerationBatchSize)
        {
            if (!await stream.MoveNextAsync())
            {
                campaign.EnumerationComplete = true;
                if (campaign.State == GoldpathCampaignState.Enumerating)
                {
                    campaign.State = GoldpathCampaignState.Running;
                }

                break;
            }

            if (campaign.EnumeratedThrough >= type.MaxTargets)
            {
                campaign.State = GoldpathCampaignState.Paused;
                campaign.LastVerb = $"goldpath: target ceiling ({type.MaxTargets}) exceeded during enumeration — narrow the selector or raise the type's ceiling, then resume or abort";
                _logger.LogWarning("Campaign {CampaignId} paused: target ceiling {MaxTargets} exceeded.", campaign.Id, type.MaxTargets);
                break;
            }

            campaign.EnumeratedThrough++;
            db.Set<GoldpathCampaignItem>().Add(new GoldpathCampaignItem
            {
                CampaignId = campaign.Id,
                Seq = campaign.EnumeratedThrough,
                TargetJson = type.SerializeTarget(stream.Current),
                State = GoldpathCampaignItemState.Pending,
            });
            if (type.KeyOf is { } keyOf)
            {
                // R2.7: the key rides the same durable write as the items it covers.
                var key = keyOf(stream.Current);
                if (string.IsNullOrEmpty(key) || key.Length > 256)
                {
                    throw new InvalidOperationException(
                        $"Campaign type '{type.Key}' produced a target key that is empty or longer than 256 characters — keys must be short and stable.");
                }

                campaign.EnumeratedKey = key;
            }

            added++;
        }

        if (campaign.EnumerationComplete && campaign.State == GoldpathCampaignState.Enumerating)
        {
            campaign.State = GoldpathCampaignState.Running;
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return added;
    }

    /// <summary>
    /// Opens the type's target stream at the campaign's watermark (takeover resume): a
    /// keyset type (R2.7) reopens AFTER the last materialized key; a count type re-reads
    /// and skips <see cref="GoldpathCampaign.EnumeratedThrough"/> rows.
    /// </summary>
    public async Task<IAsyncEnumerator<object>> OpenStreamAtWatermarkAsync(
        IServiceProvider services, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        var type = _options.Type(campaign.Type);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(campaign.ParametersJson) ?? [];
        if (type.EnumerateAfter is { } after)
        {
            return after(services, parameters, campaign.EnumeratedThrough == 0 ? null : campaign.EnumeratedKey)
                .GetAsyncEnumerator(cancellationToken);
        }

        var stream = type.Enumerate(services, parameters).GetAsyncEnumerator(cancellationToken);
        for (long skip = 0; skip < campaign.EnumeratedThrough; skip++)
        {
            if (!await stream.MoveNextAsync())
            {
                break;   // the source shrank under us; enumeration will just complete
            }
        }

        return stream;
    }

    /// <summary>
    /// Releases up to <paramref name="allowance"/> items: PUBLISH first, then the batched
    /// durable mark (a crash between the two re-publishes on takeover — the consumer's
    /// state-guarded claim makes the duplicate a no-op, never a double-send).
    /// </summary>
    public async Task<int> ReleaseBatchAsync(
        IServiceProvider services, GoldpathCampaign campaign, int allowance, CancellationToken cancellationToken)
    {
        var available = campaign.EnumeratedThrough - campaign.ReleasedThrough;
        var count = (int)Math.Min(Math.Min(allowance, _options.ReleaseBatchSize), available);
        if (count <= 0)
        {
            return 0;
        }

        var db = services.GetRequiredService<TContext>();
        var publisher = services.GetRequiredService<IPublishEndpoint>();
        var now = _time.GetUtcNow();
        var from = campaign.ReleasedThrough + 1;
        var to = campaign.ReleasedThrough + count;

        for (var seq = from; seq <= to; seq++)
        {
            await publisher.Publish(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
        }

        await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaign.Id && i.Seq >= from && i.Seq <= to && i.State == GoldpathCampaignItemState.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Released)
                .SetProperty(i => i.ReleasedAt, now), cancellationToken);

        db.Attach(campaign);
        campaign.ReleasedThrough = to;
        campaign.ReleasedToday += count;
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        GoldpathCampaignMetrics.Released(campaign.Type, count);
        return count;
    }

    /// <summary>
    /// Re-releases items whose backoff rung has passed (R1.3): re-publish, then flip
    /// AwaitingRetry back to Released — the same publish-then-mark order as ReleaseBatch,
    /// for the same crash-safety reason. Counts against the SAME tick allowance.
    /// </summary>
    public async Task<int> ReleaseRipeRetriesAsync(
        IServiceProvider services, GoldpathCampaign campaign, int allowance, CancellationToken cancellationToken)
    {
        if (allowance <= 0 || campaign.MaxAttempts <= 1)
        {
            return 0;
        }

        var db = services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        // Ripeness filters BEFORE the allowance truncates (review R3): a backlog of
        // not-yet-ripe low-Seq items must never starve a ripe item behind them. R2.3
        // persists the ripe instant per item (jittered at failure time), so the query is
        // one indexed range on (CampaignId, State, NextAttemptAt).
        var due = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.AwaitingRetry
                && i.NextAttemptAt <= now)
            .OrderBy(i => i.Seq)
            .Select(i => i.Seq)
            .Take(allowance)
            .ToArrayAsync(cancellationToken);
        if (due.Length == 0)
        {
            return 0;
        }

        var publisher = services.GetRequiredService<IPublishEndpoint>();
        foreach (var seq in due)
        {
            await publisher.Publish(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
        }

        await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaign.Id && due.Contains(i.Seq) && i.State == GoldpathCampaignItemState.AwaitingRetry)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Released)
                .SetProperty(i => i.ReleasedAt, now), cancellationToken);
        GoldpathCampaignMetrics.Released(campaign.Type, due.Length);
        return due.Length;
    }

    /// <summary>
    /// Publishes again the Released items nobody claimed within
    /// <see cref="GoldpathCampaignOptions.OrphanReleaseAfter"/> (R2.6). The message is gone
    /// — a pause made every consumer refuse the claim, the broker purged a queue, a
    /// redelivery budget ran out — and the row would otherwise sit Released forever, in
    /// flight on paper, blocking completion. Same publish-then-stamp order, same allowance,
    /// same claim guard on the other side: a message that was NOT lost after all is a
    /// duplicate delivery, and the guard drops it.
    /// </summary>
    public async Task<int> ReleaseOrphansAsync(
        IServiceProvider services, GoldpathCampaign campaign, int allowance, CancellationToken cancellationToken)
    {
        if (allowance <= 0)
        {
            return 0;
        }

        var db = services.GetRequiredService<TContext>();
        var now = _time.GetUtcNow();
        var cutoff = now - _options.OrphanReleaseAfter;
        var orphans = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.Released
                && (i.ReleasedAt == null || i.ReleasedAt < cutoff))
            .OrderBy(i => i.Seq)
            .Select(i => i.Seq)
            .Take(allowance)
            .ToArrayAsync(cancellationToken);
        if (orphans.Length == 0)
        {
            return 0;
        }

        var publisher = services.GetRequiredService<IPublishEndpoint>();
        foreach (var seq in orphans)
        {
            await publisher.Publish(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
        }

        await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaign.Id && orphans.Contains(i.Seq) && i.State == GoldpathCampaignItemState.Released)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ReleasedAt, now), cancellationToken);
        GoldpathCampaignMetrics.Orphans(campaign.Type, orphans.Length);
        _logger.LogInformation("Campaign {CampaignId}: {Count} orphaned releases published again.", campaign.Id, orphans.Length);
        return orphans.Length;
    }

    /// <summary>Rolls the quota day when the policy-timezone midnight passed (durable — survives takeover).</summary>
    public async Task RollQuotaDayIfNeededAsync(TContext db, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        var today = PolicyOf(campaign).LocalDay(_time.GetUtcNow());
        if (campaign.QuotaDay != today)
        {
            db.Attach(campaign);
            campaign.QuotaDay = today;
            campaign.ReleasedToday = 0;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// The CONSUMER's claim: state-guarded, BEFORE any external call (constraint 2). R2.6
    /// adds the campaign to the guard: a message that reaches a consumer after the campaign
    /// was paused or aborted claims nothing — pause latency is one message, not one queue.
    /// The row stays Released; the orphan sweep publishes it again on resume.
    /// A RIPE AwaitingRetry row claims too: the leader publishes BEFORE it marks (crash
    /// safety), and on a real broker the retry's message can reach a consumer before the
    /// mark lands — refusing it would drop every retry into the orphan sweep's wait.
    /// An unripe one still refuses, so a stale duplicate cannot jump the ladder.
    /// </summary>
    public async Task<GoldpathCampaignItem?> ClaimAsync(TContext db, Guid campaignId, long seq, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var claimed = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaignId && i.Seq == seq
                && (i.State == GoldpathCampaignItemState.Released || i.State == GoldpathCampaignItemState.Pending
                    || (i.State == GoldpathCampaignItemState.AwaitingRetry && i.NextAttemptAt <= now))
                && db.Set<GoldpathCampaign>().Any(c => c.Id == campaignId && c.State == GoldpathCampaignState.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Processing)
                .SetProperty(i => i.ClaimedAt, now), cancellationToken);
        if (claimed == 0)
        {
            return null;   // a duplicate delivery or a rebalance replay — someone owns it
        }

        return await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .SingleAsync(i => i.CampaignId == campaignId && i.Seq == seq, cancellationToken);
    }

    /// <summary>Runs the type's handler for one claimed item and returns its typed result (R2.1).</summary>
    public Task<GoldpathCampaignActionResult> ExecuteItemAsync(
        IServiceProvider services, string typeKey, Guid campaignId, long seq, string targetJson,
        string? tenant, int attempt, bool replay, CancellationToken cancellationToken)
        => _options.Type(typeKey).ExecuteItem(
            targetJson, new GoldpathCampaignItemContext(campaignId, seq, typeKey, tenant, attempt, replay, services), cancellationToken);

    /// <summary>Turns a handler's typed result into the outcome the sink applies (R2.1).</summary>
    public GoldpathCampaignOutcomeMessage OutcomeFor(Guid campaignId, long seq, GoldpathCampaignActionResult result)
        => result switch
        {
            GoldpathCampaignActionSucceeded => new GoldpathCampaignOutcomeMessage(campaignId, seq, true, null),
            GoldpathCampaignActionFailed failed => new GoldpathCampaignOutcomeMessage(
                campaignId, seq, false, failed.Message.Length > 1000 ? failed.Message[..1000] : failed.Message)
            {
                ErrorCode = failed.ErrorCode,
                Retryable = failed.Retryable,
            },
            GoldpathCampaignActionAccepted accepted => new GoldpathCampaignOutcomeMessage(campaignId, seq, false, null)
            {
                Accepted = true,
                CorrelationId = accepted.CorrelationId,
                AckDeadline = _time.GetUtcNow() + accepted.AckTimeout,
            },
            _ => throw new InvalidOperationException($"Unknown action result '{result.GetType().Name}'."),
        };

    /// <summary>
    /// Resolves a target system's callback (R2.1) to the outcome it means: the item of
    /// <paramref name="type"/> that carries <paramref name="correlationId"/>. Found + null
    /// outcome = already settled (idempotent repeat); not found = unknown id.
    /// </summary>
    public async Task<GoldpathCampaignCallbackResolution> ResolveCallbackAsync(
        TContext db, string type, string correlationId, GoldpathCampaignCallbackRequest request, CancellationToken cancellationToken)
    {
        var hit = await (
            from item in db.Set<GoldpathCampaignItem>().AsNoTracking()
            join campaign in db.Set<GoldpathCampaign>().AsNoTracking() on item.CampaignId equals campaign.Id
            where campaign.Type == type && item.CorrelationId == correlationId
            orderby item.Seq descending
            select new { item.CampaignId, item.Seq, item.State })
            .FirstOrDefaultAsync(cancellationToken);
        if (hit is null)
        {
            return new GoldpathCampaignCallbackResolution(false, null);
        }

        if (hit.State != GoldpathCampaignItemState.AwaitingAck)
        {
            return new GoldpathCampaignCallbackResolution(true, null);
        }

        var error = request.Error is { Length: > 1000 } oversized ? oversized[..1000] : request.Error;
        return new GoldpathCampaignCallbackResolution(true, new GoldpathCampaignOutcomeMessage(hit.CampaignId, hit.Seq, request.Succeeded, error)
        {
            ErrorCode = request.ErrorCode,
            Retryable = request.Retryable,
            CorrelationId = correlationId,
        });
    }

    /// <summary>
    /// Applies one BATCH of outcomes (the sink's flush, constraint 4): set-based item
    /// updates + relative campaign counters — never a write per item. R2.1 adds the
    /// acceptance path (Processing → AwaitingAck) and the non-retryable short-circuit;
    /// R2.3 stamps the jittered ripe instant on every retry.
    /// </summary>
    public async Task ApplyOutcomesAsync(
        TContext db, Guid campaignId, IReadOnlyList<GoldpathCampaignOutcomeMessage> outcomes, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var succeeded = outcomes.Where(o => o.Succeeded).Select(o => o.Seq).ToArray();
        var accepted = outcomes.Where(o => !o.Succeeded && o.Accepted).ToArray();
        var failed = outcomes.Where(o => !o.Succeeded && !o.Accepted).ToArray();
        var maxAttempts = failed.Length == 0
            ? 1
            : await db.Set<GoldpathCampaign>().AsNoTracking()
                .Where(c => c.Id == campaignId).Select(c => c.MaxAttempts).SingleAsync(cancellationToken);

        var settled = 0;
        if (succeeded.Length > 0)
        {
            // A success settles a claimed item OR an accepted one (the callback said yes).
            settled = await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaignId && succeeded.Contains(i.Seq)
                    && (i.State == GoldpathCampaignItemState.Processing || i.State == GoldpathCampaignItemState.AwaitingAck))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.State, GoldpathCampaignItemState.Succeeded)
                    .SetProperty(i => i.CompletedAt, now), cancellationToken);
        }

        foreach (var acceptance in accepted)
        {
            // R2.1: the target system took the request; the item parks until the callback
            // names the correlation id or the deadline passes. Still in flight.
            await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaignId && i.Seq == acceptance.Seq && i.State == GoldpathCampaignItemState.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.State, GoldpathCampaignItemState.AwaitingAck)
                    .SetProperty(i => i.CorrelationId, acceptance.CorrelationId)
                    .SetProperty(i => i.AckDeadline, acceptance.AckDeadline ?? now + _options.NextRetryDelay(1)), cancellationToken);
        }

        var exhausted = 0;
        foreach (var failure in failed)
        {
            // Failures are the RARE path by design, so the per-item read that the jittered
            // ripe instant needs is affordable. Attempts is bumped here — the outcome is the
            // moment an attempt provably happened. Counted by ROWS TOUCHED: a stray or
            // duplicate outcome whose guard matches nothing must not inflate FailedCount.
            var current = await db.Set<GoldpathCampaignItem>().AsNoTracking()
                .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq
                    && (i.State == GoldpathCampaignItemState.Processing || i.State == GoldpathCampaignItemState.AwaitingAck))
                .Select(i => new { i.Attempts, i.State })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                continue;
            }

            var attemptsAfter = current.Attempts + 1;
            if (failure.Retryable && attemptsAfter < maxAttempts)
            {
                // R1.3/R2.3: attempts LEFT → wait out the (jittered) rung, then the pacer re-releases.
                var nextAttemptAt = now + _options.NextRetryDelay(attemptsAfter);
                await db.Set<GoldpathCampaignItem>()
                    .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq && i.State == current.State && i.Attempts == current.Attempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.State, GoldpathCampaignItemState.AwaitingRetry)
                        .SetProperty(i => i.Attempts, attemptsAfter)
                        .SetProperty(i => i.CompletedAt, now)   // = last failure instant
                        .SetProperty(i => i.NextAttemptAt, nextAttemptAt)
                        .SetProperty(i => i.CorrelationId, (string?)null)
                        .SetProperty(i => i.AckDeadline, (DateTimeOffset?)null)
                        .SetProperty(i => i.Error, failure.Error)
                        .SetProperty(i => i.ErrorCode, failure.ErrorCode), cancellationToken);
            }
            else
            {
                // Exhausted, or the provider said a retry cannot help (R2.1): durable failure, once.
                exhausted += await db.Set<GoldpathCampaignItem>()
                    .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq && i.State == current.State && i.Attempts == current.Attempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.State, GoldpathCampaignItemState.Failed)
                        .SetProperty(i => i.Attempts, attemptsAfter)
                        .SetProperty(i => i.CompletedAt, now)
                        .SetProperty(i => i.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(i => i.Error, failure.Error)
                        .SetProperty(i => i.ErrorCode, failure.ErrorCode), cancellationToken);
            }
        }

        await db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.SucceededCount, c => c.SucceededCount + settled)
                .SetProperty(c => c.FailedCount, c => c.FailedCount + exhausted), cancellationToken);
        GoldpathCampaignMetrics.Outcomes(campaignId, settled, exhausted);
    }

    /// <summary>
    /// The ack sweep (R2.1): an accepted item whose deadline passed without a callback is a
    /// RETRYABLE failure — the ladder applies, exhaustion lands in the repair queue. Never a
    /// silent success. Bounded per tick; returns how many were swept.
    /// </summary>
    public async Task<int> SweepAckTimeoutsAsync(TContext db, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var overdue = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.AwaitingAck && i.AckDeadline < now)
            .OrderBy(i => i.Seq)
            .Select(i => i.Seq)
            .Take(100)
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return 0;
        }

        await ApplyOutcomesAsync(db, campaign.Id,
            [.. overdue.Select(seq => new GoldpathCampaignOutcomeMessage(campaign.Id, seq, false, "no callback before the ack deadline")
            {
                ErrorCode = "ACK_TIMEOUT",
                Retryable = true,
            })], cancellationToken);
        GoldpathCampaignMetrics.AckTimeouts(campaign.Type, overdue.Count);
        _logger.LogWarning("Campaign {CampaignId}: {Count} accepted items passed their ack deadline without a callback.", campaign.Id, overdue.Count);
        return overdue.Count;
    }

    /// <summary>
    /// Flips a fully-terminal campaign to its completion state (state-token guarded).
    /// Returns the terminal state THIS call stamped, null when nothing changed — the
    /// pacer files the failed set into the repair queue exactly once off that signal.
    /// </summary>
    public async Task<GoldpathCampaignState?> TryCompleteAsync(TContext db, Guid campaignId, CancellationToken cancellationToken)
    {
        try
        {
            var campaign = await db.Set<GoldpathCampaign>().FirstAsync(c => c.Id == campaignId, cancellationToken);
            if (campaign.State != GoldpathCampaignState.Running || !campaign.EnumerationComplete)
            {
                return null;
            }

            var terminal = campaign.SucceededCount + campaign.FailedCount;
            if (campaign.ReleasedThrough < campaign.EnumeratedThrough || terminal < campaign.EnumeratedThrough)
            {
                return null;
            }

            campaign.State = campaign.FailedCount == 0 ? GoldpathCampaignState.Completed : GoldpathCampaignState.CompletedWithFailures;
            campaign.CompletedAt = _time.GetUtcNow();
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Campaign {CampaignId} finished as {State}.", campaignId, campaign.State);
                return campaign.State;
            }
            catch (DbUpdateConcurrencyException)
            {
                return null;   // a verb raced us; the pacer re-evaluates next tick
            }
        }
        finally
        {
            db.ChangeTracker.Clear();   // never leak a tracked campaign into the caller's context
        }
    }

    /// <summary>Replays one FAILED item (the jobs replay-items verb): re-claim, re-execute, apply inline.</summary>
    public async Task ReplayItemAsync(IServiceProvider services, string itemKey, CancellationToken cancellationToken)
    {
        var separator = itemKey.IndexOf('#');
        var campaignId = Guid.ParseExact(itemKey[..separator], "N");
        var seq = long.Parse(itemKey[(separator + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var db = services.GetRequiredService<TContext>();
        var campaign = await db.Set<GoldpathCampaign>().AsNoTracking().SingleAsync(c => c.Id == campaignId, cancellationToken);
        var item = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .SingleOrDefaultAsync(i => i.CampaignId == campaignId && i.Seq == seq, cancellationToken)
            ?? throw new InvalidOperationException($"No campaign item for repair item '{itemKey}'.");
        if (item.State == GoldpathCampaignItemState.Succeeded)
        {
            return;   // idempotent evidence, not a re-send
        }

        await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaignId && i.Seq == seq)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, GoldpathCampaignItemState.Processing), cancellationToken);
        // The item LEFT the failed set the moment it was re-claimed; the apply below counts
        // it again only if it fails again (exhausted at MaxAttempts → +1), so the ledger heals.
        await db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedCount, c => c.FailedCount - 1), cancellationToken);
        var result = await ExecuteItemAsync(services, campaign.Type, campaignId, seq, item.TargetJson, campaign.Tenant,
            attempt: item.Attempts + 1, replay: true, cancellationToken);
        await ApplyOutcomesAsync(db, campaignId, [OutcomeFor(campaignId, seq, result)], cancellationToken);
    }
}
