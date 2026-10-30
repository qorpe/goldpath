using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The campaign engine (campaign RFC §2): instance creation, streaming enumeration under
/// the ceiling, policy-governed release, outcome application, completion math. The engine
/// owns STATE; leadership/scheduling belongs to the pacer run, delivery to the message seam.
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
            // R3.1: Npgsql refuses a non-zero offset on timestamptz, so the instant is stored in UTC.
            StartAt = effective.StartAt?.ToUniversalTime(),
            MaxAwaitingAck = effective.MaxAwaitingAck,
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

    /// <summary>
    /// The campaigns a leader slice works: Created (to start), Enumerating, Running, and Ready
    /// (R3.1), so the pacer can start a scheduled campaign at its start and expire it at its end date.
    /// </summary>
    public async Task<List<GoldpathCampaign>> LoadWorkableAsync(TContext db, CancellationToken cancellationToken)
        => await db.Set<GoldpathCampaign>()
            .Where(c => c.State == GoldpathCampaignState.Created
                || c.State == GoldpathCampaignState.Enumerating
                || c.State == GoldpathCampaignState.Running
                || c.State == GoldpathCampaignState.Ready)
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
            StartAt = campaign.StartAt,
            MaxAwaitingAck = campaign.MaxAwaitingAck,
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
                    campaign.State = SnapshotDoneState(campaign);
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
            campaign.State = SnapshotDoneState(campaign);
        }

        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return added;
    }

    /// <summary>
    /// Where a finished snapshot goes (R3.1): Ready while the start is still ahead, Running
    /// otherwise, exactly as R2 did. A start that passed during the snapshot starts it at once.
    /// </summary>
    private GoldpathCampaignState SnapshotDoneState(GoldpathCampaign campaign)
        => PolicyOf(campaign).IsBeforeStart(_time.GetUtcNow()) ? GoldpathCampaignState.Ready : GoldpathCampaignState.Running;

    /// <summary>
    /// Opens the type's target stream at the campaign's watermark (takeover resume): a
    /// keyset type (R2.7) reopens AFTER the last materialized key; a count type re-reads
    /// and skips <see cref="GoldpathCampaign.EnumeratedThrough"/> rows. A keyset type
    /// resuming a row that carries NO key — enumerated before R2, or while the type was
    /// count-based — derives the key from the target it materialized last; reopening from
    /// the start would materialize, and send, every one of those targets a second time.
    /// </summary>
    public async Task<IAsyncEnumerator<object>> OpenStreamAtWatermarkAsync(
        IServiceProvider services, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        var type = _options.Type(campaign.Type);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(campaign.ParametersJson) ?? [];
        if (type.EnumerateAfter is { } after)
        {
            var resumeKey = campaign.EnumeratedThrough == 0
                ? null
                : campaign.EnumeratedKey ?? await DeriveResumeKeyAsync(services, campaign, type, cancellationToken);
            return after(services, parameters, resumeKey).GetAsyncEnumerator(cancellationToken);
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
    /// The resume key of a keyset campaign whose row carries none (R2 review #3): the key of
    /// the target materialized LAST, read off its item row. Same stable order the selector
    /// promises, so "after it" is exactly where enumeration stopped. A row that cannot name
    /// its key refuses the takeover — a guess here is a double send.
    /// </summary>
    private async Task<string> DeriveResumeKeyAsync(
        IServiceProvider services, GoldpathCampaign campaign, GoldpathCampaignType type, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<TContext>();
        var last = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.Seq == campaign.EnumeratedThrough)
            .Select(i => i.TargetJson)
            .SingleOrDefaultAsync(cancellationToken);
        string? key = null;
        Exception? cause = null;
        try
        {
            key = last is null ? null : type.KeyOfStoredTarget!(last);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            cause = e;
        }

        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                $"goldpath: campaign {campaign.Id} ('{type.Key}') resumes by key but carries none, and item #{campaign.EnumeratedThrough} "
                + "cannot name its resume key — reopening the selector from the start would send every materialized target twice. "
                + "Set GoldpathCampaigns.EnumeratedKey to that target's key, or abort and recreate the campaign.", cause);
        }

        _logger.LogInformation("Campaign {CampaignId}: resume key derived from item #{Seq} (the row carried none).", campaign.Id, campaign.EnumeratedThrough);
        return key;
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
        var publisher = services.GetRequiredService<IIntegrationEventPublisher>();
        var now = _time.GetUtcNow();
        var from = campaign.ReleasedThrough + 1;
        var to = campaign.ReleasedThrough + count;

        for (var seq = from; seq <= to; seq++)
        {
            await publisher.PublishAsync(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
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
        // one indexed range on (CampaignId, State, NextAttemptAt). A row with NO instant
        // predates the column (R1 wrote AwaitingRetry without one): it is ripe — the
        // CampaignR2 backfill stamps its R1 rung, and this is the net if that step was missed.
        var due = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.AwaitingRetry
                && (i.NextAttemptAt == null || i.NextAttemptAt <= now))
            .OrderBy(i => i.Seq)
            .Select(i => i.Seq)
            .Take(allowance)
            .ToArrayAsync(cancellationToken);
        if (due.Length == 0)
        {
            return 0;
        }

        var publisher = services.GetRequiredService<IIntegrationEventPublisher>();
        foreach (var seq in due)
        {
            await publisher.PublishAsync(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
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

        var publisher = services.GetRequiredService<IIntegrationEventPublisher>();
        foreach (var seq in orphans)
        {
            await publisher.PublishAsync(new GoldpathCampaignItemMessage(campaign.Id, seq, campaign.Type), cancellationToken);
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
    /// An unripe one still refuses, so a stale duplicate cannot jump the ladder; a row with no
    /// ripe instant predates R2 and is ripe by the same rule the release query reads.
    /// </summary>
    public async Task<GoldpathCampaignItem?> ClaimAsync(TContext db, Guid campaignId, long seq, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var claimed = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaignId && i.Seq == seq
                && (i.State == GoldpathCampaignItemState.Released || i.State == GoldpathCampaignItemState.Pending
                    || (i.State == GoldpathCampaignItemState.AwaitingRetry && (i.NextAttemptAt == null || i.NextAttemptAt <= now)))
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

    /// <summary>Turns a handler's typed result for <paramref name="attempt"/> into the outcome the sink applies (R2.1).</summary>
    public GoldpathCampaignOutcomeMessage OutcomeFor(Guid campaignId, long seq, int attempt, GoldpathCampaignActionResult result)
        => result switch
        {
            GoldpathCampaignActionSucceeded => new GoldpathCampaignOutcomeMessage(campaignId, seq, true, null) { Attempt = attempt },
            GoldpathCampaignActionFailed failed => new GoldpathCampaignOutcomeMessage(
                campaignId, seq, false, failed.Message.Length > 1000 ? failed.Message[..1000] : failed.Message)
            {
                ErrorCode = failed.ErrorCode,
                Retryable = failed.Retryable,
                Attempt = attempt,
            },
            GoldpathCampaignActionAccepted accepted => new GoldpathCampaignOutcomeMessage(campaignId, seq, false, null)
            {
                Accepted = true,
                CorrelationId = accepted.CorrelationId,
                AckDeadline = _time.GetUtcNow() + accepted.AckTimeout,
                Attempt = attempt,
            },
            _ => throw new InvalidOperationException($"Unknown action result '{result.GetType().Name}'."),
        };

    /// <summary>
    /// Resolves a target system's callback (R2.1) to what it means, inside ONE campaign (the
    /// route names it — R2 review #1): the item of <paramref name="campaignId"/> that awaits
    /// <paramref name="correlationId"/> gets an outcome to publish; an item that carries the
    /// id but already settled makes the callback an idempotent repeat. A callback can outrun
    /// its acceptance — the device answered while the handler's call was still returning, or
    /// before the sink flushed — so an id nobody carries YET, on a campaign with a claim in
    /// flight, is PARKED and the sink settles it the moment it writes the acceptance. Park,
    /// then look again: whichever of the two writes lands second sees the first.
    /// </summary>
    public async Task<GoldpathCampaignCallbackResolution> ResolveCallbackAsync(
        TContext db, string type, Guid campaignId, string correlationId, GoldpathCampaignCallbackRequest request, CancellationToken cancellationToken)
    {
        var campaignType = await db.Set<GoldpathCampaign>().AsNoTracking()
            .Where(c => c.Id == campaignId).Select(c => c.Type).SingleOrDefaultAsync(cancellationToken);
        if (campaignType is null || !string.Equals(campaignType, type, StringComparison.Ordinal))
        {
            return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.Unknown, null);
        }

        var error = request.Error is { Length: > 1000 } oversized ? oversized[..1000] : request.Error;
        if (await PendingOutcomeAsync(db, campaignId, correlationId, request, error, cancellationToken) is { } pending)
        {
            return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.Pending, pending);
        }

        if (await db.Set<GoldpathCampaignItem>().AnyAsync(i => i.CampaignId == campaignId && i.CorrelationId == correlationId, cancellationToken))
        {
            return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.AlreadySettled, null);
        }

        // Nobody carries the id. Only a claim in flight can still accept it; with none, the id is unknown.
        if (!await db.Set<GoldpathCampaignItem>().AnyAsync(
                i => i.CampaignId == campaignId && i.State == GoldpathCampaignItemState.Processing, cancellationToken))
        {
            return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.Unknown, null);
        }

        try
        {
            db.Set<GoldpathCampaignParkedCallback>().Add(new GoldpathCampaignParkedCallback
            {
                CampaignId = campaignId,
                CorrelationId = correlationId,
                Succeeded = request.Succeeded,
                ErrorCode = request.ErrorCode is { Length: > 64 } code ? code[..64] : request.ErrorCode,
                Error = error,
                Retryable = request.Retryable,
                ReceivedAt = _time.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same early answer twice: the first one parked stands.
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        // The sink may have written the acceptance between the first look and the park.
        if (await PendingOutcomeAsync(db, campaignId, correlationId, request, error, cancellationToken) is { } landed)
        {
            await db.Set<GoldpathCampaignParkedCallback>()
                .Where(p => p.CampaignId == campaignId && p.CorrelationId == correlationId)
                .ExecuteDeleteAsync(cancellationToken);
            return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.Pending, landed);
        }

        GoldpathCampaignMetrics.CallbacksParked(type, 1);
        return new GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus.Parked, null);
    }

    /// <summary>The outcome a callback means for the item of the campaign awaiting <paramref name="correlationId"/>; null when none awaits it.</summary>
    private static async Task<GoldpathCampaignOutcomeMessage?> PendingOutcomeAsync(
        TContext db, Guid campaignId, string correlationId, GoldpathCampaignCallbackRequest request, string? error, CancellationToken cancellationToken)
    {
        var awaiting = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaignId && i.CorrelationId == correlationId && i.State == GoldpathCampaignItemState.AwaitingAck)
            .Select(i => new { i.Seq, i.Attempts })
            .FirstOrDefaultAsync(cancellationToken);
        return awaiting is null
            ? null
            : new GoldpathCampaignOutcomeMessage(campaignId, awaiting.Seq, request.Succeeded, request.Succeeded ? null : error)
            {
                ErrorCode = request.ErrorCode,
                Retryable = request.Retryable,
                CorrelationId = correlationId,
                Attempt = awaiting.Attempts + 1,
            };
    }

    /// <summary>The states a success settles (R2.1), each updated on its own so the flush knows which rows left <c>AwaitingAck</c> (R3.4).</summary>
    private static readonly GoldpathCampaignItemState[] SettleableStates =
        [GoldpathCampaignItemState.Processing, GoldpathCampaignItemState.AwaitingAck];

    /// <summary>
    /// Re-counts <see cref="GoldpathCampaign.AwaitingAckCount"/> from the items (R3.4): the sink's
    /// running count is a cache (D3), and the leader corrects any drift once per leadership slice.
    /// One set-based update over the campaigns that hold work in flight or a non-zero count; R2's
    /// <c>(CampaignId, State, …)</c> indexes serve the count. Returns how many rows it rewrote.
    /// </summary>
    internal async Task<int> RecountAwaitingAckAsync(TContext db, CancellationToken cancellationToken)
        => await db.Set<GoldpathCampaign>()
            .Where(c => c.ReleasedThrough > c.SucceededCount + c.FailedCount || c.AwaitingAckCount != 0)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AwaitingAckCount, c => db.Set<GoldpathCampaignItem>()
                .LongCount(i => i.CampaignId == c.Id && i.State == GoldpathCampaignItemState.AwaitingAck)), cancellationToken);

    /// <summary>
    /// Applies one BATCH of outcomes (the sink's flush, constraint 4): set-based item
    /// updates + relative campaign counters — never a write per item. R2.1 adds the
    /// acceptance path (Processing → AwaitingAck) and the non-retryable short-circuit;
    /// R2.3 stamps the jittered ripe instant on every retry. Every update is bound to the
    /// ATTEMPT its outcome describes (R2 review #2): the row must still be on that attempt
    /// (<c>Attempts == Attempt - 1</c>), so a redelivered outcome of an earlier attempt is
    /// a no-op instead of a verdict on the attempt now in flight. R3.4: the same flush keeps
    /// <see cref="GoldpathCampaign.AwaitingAckCount"/>, by the rows it moved into and out of
    /// <c>AwaitingAck</c>; the ack sweep comes through here too.
    /// </summary>
    public async Task ApplyOutcomesAsync(
        TContext db, Guid campaignId, IReadOnlyList<GoldpathCampaignOutcomeMessage> outcomes, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var accepted = outcomes.Where(o => !o.Succeeded && o.Accepted).ToArray();
        var failed = outcomes.Where(o => !o.Succeeded && !o.Accepted).ToArray();
        var policy = failed.Length == 0
            ? null
            : await db.Set<GoldpathCampaign>().AsNoTracking()
                .Where(c => c.Id == campaignId).Select(c => new { c.MaxAttempts, c.State }).SingleAsync(cancellationToken);
        // The ladder runs only while the pacer still releases this campaign (or will after a
        // resume). On a finished, aborted or expired campaign a retryable failure — an ack
        // timeout, a replay that failed again — is terminal: AwaitingRetry there would wait
        // for a release that never comes (R2 review #8).
        var maxAttempts = policy is null
            ? 1
            : policy.State is GoldpathCampaignState.Created or GoldpathCampaignState.Enumerating
                or GoldpathCampaignState.Running or GoldpathCampaignState.Paused or GoldpathCampaignState.Ready
                ? policy.MaxAttempts
                : 0;

        // R3.4: AwaitingAckCount moves by the ROWS this flush moved into and out of AwaitingAck,
        // never by the outcomes it was handed — a duplicate or stale outcome whose guard matches
        // nothing moves no row, so it moves no count either.
        var enteredAck = 0;
        var leftAck = 0;
        var settled = 0;
        foreach (var attempt in outcomes.Where(o => o.Succeeded).GroupBy(o => o.Attempt))
        {
            // A success settles a claimed item OR an accepted one (the callback said yes) —
            // set-based per attempt number and prior state, and there are only ever a handful of those.
            var seqs = attempt.Select(o => o.Seq).ToArray();
            var anyAttempt = attempt.Key == 0;
            var prior = attempt.Key - 1;
            foreach (var from in SettleableStates)
            {
                var moved = await db.Set<GoldpathCampaignItem>()
                    .Where(i => i.CampaignId == campaignId && seqs.Contains(i.Seq) && i.State == from
                        && (anyAttempt || i.Attempts == prior))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.State, GoldpathCampaignItemState.Succeeded)
                        .SetProperty(i => i.CompletedAt, now), cancellationToken);
                settled += moved;
                leftAck += from == GoldpathCampaignItemState.AwaitingAck ? moved : 0;
            }
        }

        foreach (var acceptance in accepted)
        {
            // R2.1: the target system took the request; the item parks until the callback
            // names the correlation id or the deadline passes. Still in flight.
            var anyAttempt = acceptance.Attempt == 0;
            var prior = acceptance.Attempt - 1;
            enteredAck += await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaignId && i.Seq == acceptance.Seq && i.State == GoldpathCampaignItemState.Processing
                    && (anyAttempt || i.Attempts == prior))
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
            var anyAttempt = failure.Attempt == 0;
            var prior = failure.Attempt - 1;
            var current = await db.Set<GoldpathCampaignItem>().AsNoTracking()
                .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq
                    && (i.State == GoldpathCampaignItemState.Processing || i.State == GoldpathCampaignItemState.AwaitingAck)
                    && (anyAttempt || i.Attempts == prior))
                .Select(i => new { i.Attempts, i.State })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                continue;
            }

            var attemptsAfter = current.Attempts + 1;
            int moved;
            if (failure.Retryable && attemptsAfter < maxAttempts)
            {
                // R1.3/R2.3: attempts LEFT → wait out the (jittered) rung, then the pacer re-releases.
                var nextAttemptAt = now + _options.NextRetryDelay(attemptsAfter);
                moved = await db.Set<GoldpathCampaignItem>()
                    .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq && i.State == current.State && i.Attempts == current.Attempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.State, GoldpathCampaignItemState.AwaitingRetry)
                        .SetProperty(i => i.Attempts, attemptsAfter)
                        .SetProperty(i => i.CompletedAt, now)   // = last failure instant
                        .SetProperty(i => i.NextAttemptAt, nextAttemptAt)
                        // The spent acceptance's id STAYS: a repeat of the webhook that failed it
                        // finds the row and answers 200 (R2.1 "idempotent") instead of 404; the
                        // next acceptance overwrites it.
                        .SetProperty(i => i.AckDeadline, (DateTimeOffset?)null)
                        .SetProperty(i => i.Error, failure.Error)
                        .SetProperty(i => i.ErrorCode, failure.ErrorCode), cancellationToken);
            }
            else
            {
                // Exhausted, or the provider said a retry cannot help (R2.1): durable failure, once.
                moved = await db.Set<GoldpathCampaignItem>()
                    .Where(i => i.CampaignId == campaignId && i.Seq == failure.Seq && i.State == current.State && i.Attempts == current.Attempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.State, GoldpathCampaignItemState.Failed)
                        .SetProperty(i => i.Attempts, attemptsAfter)
                        .SetProperty(i => i.CompletedAt, now)
                        .SetProperty(i => i.NextAttemptAt, (DateTimeOffset?)null)
                        .SetProperty(i => i.Error, failure.Error)
                        .SetProperty(i => i.ErrorCode, failure.ErrorCode), cancellationToken);
                exhausted += moved;
            }

            leftAck += current.State == GoldpathCampaignItemState.AwaitingAck ? moved : 0;
        }

        var ackDelta = enteredAck - leftAck;
        await db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.SucceededCount, c => c.SucceededCount + settled)
                .SetProperty(c => c.FailedCount, c => c.FailedCount + exhausted)
                .SetProperty(c => c.AwaitingAckCount, c => c.AwaitingAckCount + ackDelta), cancellationToken);
        GoldpathCampaignMetrics.Outcomes(campaignId, settled, exhausted);

        if (accepted.Length > 0)
        {
            await SettleParkedCallbacksAsync(db, campaignId, accepted, cancellationToken);
        }
    }

    /// <summary>
    /// The other half of parking (R2 review #1): the acceptances this flush just wrote may
    /// have been answered already. One query per flush finds those answers; each becomes the
    /// outcome of the attempt it answers (attempt-bound, so a stale park cannot settle a
    /// later attempt), is applied, and leaves the parking lot.
    /// </summary>
    private async Task SettleParkedCallbacksAsync(
        TContext db, Guid campaignId, GoldpathCampaignOutcomeMessage[] accepted, CancellationToken cancellationToken)
    {
        var correlations = accepted.Select(a => a.CorrelationId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var parked = await db.Set<GoldpathCampaignParkedCallback>().AsNoTracking()
            .Where(p => p.CampaignId == campaignId && correlations.Contains(p.CorrelationId))
            .ToListAsync(cancellationToken);
        if (parked.Count == 0)
        {
            return;
        }

        var answers = parked.Select(p =>
        {
            var acceptance = accepted.Last(a => string.Equals(a.CorrelationId, p.CorrelationId, StringComparison.Ordinal));
            return new GoldpathCampaignOutcomeMessage(campaignId, acceptance.Seq, p.Succeeded, p.Succeeded ? null : p.Error)
            {
                ErrorCode = p.ErrorCode,
                Retryable = p.Retryable,
                CorrelationId = p.CorrelationId,
                Attempt = acceptance.Attempt,
            };
        }).ToArray();
        await ApplyOutcomesAsync(db, campaignId, answers, cancellationToken);
        var settled = parked.Select(p => p.CorrelationId).ToArray();
        await db.Set<GoldpathCampaignParkedCallback>()
            .Where(p => p.CampaignId == campaignId && settled.Contains(p.CorrelationId))
            .ExecuteDeleteAsync(cancellationToken);
        _logger.LogInformation("Campaign {CampaignId}: {Count} callbacks that outran their acceptance settled on arrival of it.", campaignId, parked.Count);
    }

    /// <summary>
    /// Drops parked callbacks no acceptance claimed within <see cref="GoldpathCampaignOptions.StaleClaimAfter"/>
    /// — by then the claim they were waiting for was swept as interrupted anyway. Returns how many went.
    /// </summary>
    public async Task<int> ExpireParkedCallbacksAsync(TContext db, CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow() - _options.StaleClaimAfter;
        return await db.Set<GoldpathCampaignParkedCallback>().Where(p => p.ReceivedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// The ack sweep (R2.1): an accepted item whose deadline passed without a callback is a
    /// RETRYABLE failure — the ladder applies, exhaustion lands in the repair queue. Never a
    /// silent success. On a campaign the pacer no longer releases (finished, aborted,
    /// expired) the timeout is terminal at once. Bounded per call; returns the swept items.
    /// </summary>
    public async Task<IReadOnlyList<long>> SweepAckTimeoutsAsync(TContext db, GoldpathCampaign campaign, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var overdue = await db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == campaign.Id && i.State == GoldpathCampaignItemState.AwaitingAck && i.AckDeadline < now)
            .OrderBy(i => i.Seq)
            .Select(i => new { i.Seq, i.Attempts })
            .Take(100)
            .ToListAsync(cancellationToken);
        if (overdue.Count == 0)
        {
            return [];
        }

        await ApplyOutcomesAsync(db, campaign.Id,
            [.. overdue.Select(item => new GoldpathCampaignOutcomeMessage(campaign.Id, item.Seq, false, "no callback before the ack deadline")
            {
                ErrorCode = "ACK_TIMEOUT",
                Retryable = true,
                Attempt = item.Attempts + 1,   // the attempt that timed out — a callback racing the sweep settles it, or this does
            })], cancellationToken);
        GoldpathCampaignMetrics.AckTimeouts(campaign.Type, overdue.Count);
        _logger.LogWarning("Campaign {CampaignId}: {Count} accepted items passed their ack deadline without a callback.", campaign.Id, overdue.Count);
        return [.. overdue.Select(item => item.Seq)];
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

    /// <summary>
    /// Replays one FAILED item (the jobs replay-items verb): re-claim, re-execute, apply inline.
    /// Only a Failed row can be re-claimed — an item a consumer owns, or one waiting on its
    /// ladder or its callback, is never executed a second time by the repair verb. A handler
    /// that throws puts the item back exactly as it was (Failed, counted), so a re-driven
    /// replay can never drift the ledger (R2 review, finding 5).
    /// </summary>
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

        // State-guarded like the consumer's claim (constraint 2): Failed → Processing, or nothing.
        var claimed = await db.Set<GoldpathCampaignItem>()
            .Where(i => i.CampaignId == campaignId && i.Seq == seq && i.State == GoldpathCampaignItemState.Failed
                && i.Attempts == item.Attempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.State, GoldpathCampaignItemState.Processing)
                .SetProperty(i => i.ClaimedAt, _time.GetUtcNow()), cancellationToken);
        if (claimed == 0)
        {
            _logger.LogInformation("Repair item {ItemKey} is {State}, not Failed — someone owns it; nothing replayed.", itemKey, item.State);
            return;
        }

        // The item LEFT the failed set the moment it was re-claimed; the apply below counts
        // it again only if it fails again (exhausted → +1), so the ledger heals on the OUTCOME.
        await db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedCount, c => c.FailedCount - 1), cancellationToken);
        var attempt = item.Attempts + 1;
        GoldpathCampaignActionResult result;
        try
        {
            result = await ExecuteItemAsync(services, campaign.Type, campaignId, seq, item.TargetJson, campaign.Tenant,
                attempt, replay: true, cancellationToken);
        }
        catch
        {
            // No outcome exists, so nothing happened as far as the ledger knows: put the item
            // back in the failed set it came from, then let the runner record the throw.
            await db.Set<GoldpathCampaignItem>()
                .Where(i => i.CampaignId == campaignId && i.Seq == seq && i.State == GoldpathCampaignItemState.Processing
                    && i.Attempts == item.Attempts)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.State, GoldpathCampaignItemState.Failed), CancellationToken.None);
            await db.Set<GoldpathCampaign>().Where(c => c.Id == campaignId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.FailedCount, c => c.FailedCount + 1), CancellationToken.None);
            throw;
        }

        await ApplyOutcomesAsync(db, campaignId, [OutcomeFor(campaignId, seq, attempt, result)], cancellationToken);
    }
}
