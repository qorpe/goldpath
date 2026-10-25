using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The competing item handler (campaign RFC D4): CLAIMS the item row (state-guarded) BEFORE
/// the handler's external call — a broker redelivery or a rebalance replay claims zero
/// rows and drops silently; a double-send is structurally impossible (constraint 2).
/// Outcomes are PUBLISHED, not written — the sink batches durable truth (constraint 4).
/// R2.1: the handler's typed result decides the outcome's shape (terminal, retryable or
/// accepted-pending-callback); a thrown exception is still a retryable failure.
/// It runs through the consume seam (RFC goldpath-messaging-engines §4.1 row 11), so it names no engine.
/// </summary>
public sealed class GoldpathCampaignItemHandler<TContext> : IIntegrationEventHandler<GoldpathCampaignItemMessage>
    where TContext : DbContext
{
    private readonly GoldpathCampaignEngine<TContext> _engine;
    private readonly TContext _db;
    private readonly IServiceProvider _services;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly ILogger<GoldpathCampaignItemHandler<TContext>> _logger;

    /// <summary>Resolved per message (scoped).</summary>
    public GoldpathCampaignItemHandler(
        GoldpathCampaignEngine<TContext> engine, TContext db, IServiceProvider services,
        IIntegrationEventPublisher publisher, ILogger<GoldpathCampaignItemHandler<TContext>> logger)
    {
        _engine = engine;
        _db = db;
        _services = services;
        _publisher = publisher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(GoldpathCampaignItemMessage integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        var message = integrationEvent;
        var item = await _engine.ClaimAsync(_db, message.CampaignId, message.Seq, cancellationToken);
        if (item is null)
        {
            return;   // duplicate delivery, or a paused campaign — someone owns it, or nobody should; dropping IS the correctness
        }

        var campaign = await _db.Set<GoldpathCampaign>().AsNoTracking()
            .SingleAsync(c => c.Id == message.CampaignId, cancellationToken);
        GoldpathCampaignOutcomeMessage outcome;
        var attempt = item.Attempts + 1;
        try
        {
            var result = await _engine.ExecuteItemAsync(
                _services, message.Type, message.CampaignId, message.Seq, item.TargetJson,
                campaign.Tenant, attempt, replay: false, cancellationToken);
            outcome = _engine.OutcomeFor(message.CampaignId, message.Seq, attempt, result);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Campaign item {CampaignId}#{Seq} failed in its handler.", message.CampaignId, message.Seq);
            outcome = new GoldpathCampaignOutcomeMessage(
                message.CampaignId, message.Seq, false, e.Message.Length > 1000 ? e.Message[..1000] : e.Message)
            {
                ErrorCode = "HANDLER_EXCEPTION",
                Retryable = true,
                Attempt = attempt,
            };
        }

        // Inside a delivery the seam's publisher publishes from that delivery, as the consume context did.
        await _publisher.PublishAsync(outcome, cancellationToken);
    }
}

/// <summary>
/// The outcome SINK (campaign RFC D5, constraint 4): takes outcome BATCHES and flushes
/// set-based durable updates — 30M items never mean 30M per-item writes. A batch handler on the
/// seam (RFC goldpath-messaging-engines §4.1 row 12), grouped by <see cref="GoldpathCampaignEndpoints.OutcomeBatch"/>.
/// </summary>
public sealed class GoldpathCampaignOutcomeHandler<TContext> : IIntegrationEventBatchHandler<GoldpathCampaignOutcomeMessage>
    where TContext : DbContext
{
    private readonly GoldpathCampaignEngine<TContext> _engine;
    private readonly TContext _db;

    /// <summary>Resolved per batch (scoped).</summary>
    public GoldpathCampaignOutcomeHandler(GoldpathCampaignEngine<TContext> engine, TContext db)
    {
        _engine = engine;
        _db = db;
    }

    /// <inheritdoc />
    public async Task HandleAsync(IReadOnlyList<IntegrationEvent<GoldpathCampaignOutcomeMessage>> batch, CancellationToken cancellationToken = default)
    {
        foreach (var group in batch.GroupBy(m => m.Message.CampaignId))
        {
            await _engine.ApplyOutcomesAsync(_db, group.Key, [.. group.Select(m => m.Message)], cancellationToken);
        }
    }
}

/// <summary>Where campaign's two handlers listen, and how the sink's batches are grouped.</summary>
internal static partial class GoldpathCampaignEndpoints
{
    /// <summary>
    /// The sink's grouping — the numbers it has always run with: 200 outcomes or 500 ms, one batch at a time,
    /// so the set-based flushes of one campaign never race each other.
    /// </summary>
    public static GoldpathBatchOptions OutcomeBatch => new()
    {
        MessageLimit = 200,
        TimeLimit = TimeSpan.FromMilliseconds(500),
        ConcurrencyLimit = 1,
    };

    /// <summary>
    /// The queue both handlers drain: the name a running fleet already has. The consumers this replaces were
    /// generic over the context, and the kebab-case formatter names a generic consumer after its last type
    /// argument — so both shared one queue named after <typeparamref name="TContext"/> (<c>OrdersDbContext</c>
    /// drains <c>orders-db-context</c>). Kept byte for byte, so a rolling deployment drains one queue
    /// (RFC goldpath-messaging-engines §4.1 row 11, S4). It is not <see cref="GoldpathHandlerEndpoints.NameOf"/>:
    /// that splits an acronym differently (<c>V2DB</c>), and a queue that moves strands its messages.
    /// </summary>
    public static string QueueOf<TContext>() => QueueOf(typeof(TContext));

    /// <inheritdoc cref="QueueOf{TContext}"/>
    internal static string QueueOf(Type context)
        => Boundary().Replace(context.Name, m => "-" + m.Value).ToLowerInvariant().Replace('_', '-');

    [GeneratedRegex("(?<=[a-z0-9])[A-Z]")]
    private static partial Regex Boundary();
}
