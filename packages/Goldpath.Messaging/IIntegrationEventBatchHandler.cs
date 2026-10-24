using MassTransit;

namespace Goldpath;

/// <summary>
/// The batch half of the consume seam (RFC goldpath-messaging-engines §4.1 row 12): a handler that takes
/// deliveries in groups, for a consumer whose work is set-based — a sink that turns a thousand outcomes into
/// one write rather than a thousand. Every engine must deliver to it on the same terms: batches bounded by
/// <see cref="GoldpathBatchOptions.MessageLimit"/> OR <see cref="GoldpathBatchOptions.TimeLimit"/>, whichever
/// comes first; at most <see cref="GoldpathBatchOptions.ConcurrencyLimit"/> batches in flight per endpoint;
/// and a throw fails the WHOLE batch into the retry ladder, so the handler must be idempotent per message.
/// </summary>
/// <typeparam name="TEvent">The event — must carry the <see cref="IIntegrationEvent"/> marker.</typeparam>
public interface IIntegrationEventBatchHandler<TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <summary>Handles one batch; never empty, in delivery order as far as the transport keeps one.</summary>
    Task HandleAsync(IReadOnlyList<IntegrationEvent<TEvent>> batch, CancellationToken cancellationToken = default);
}

/// <summary>One delivery inside a batch: the event and what the pipeline knows about it.</summary>
/// <param name="Message">The event as delivered.</param>
/// <param name="Context">Its identity, correlation, tenant and headers — the same shape a single handler receives.</param>
public sealed record IntegrationEvent<TEvent>(TEvent Message, IntegrationEventContext Context)
    where TEvent : class, IIntegrationEvent;

/// <summary>
/// How a batch handler's deliveries are grouped (RFC goldpath-messaging-engines §4.1 row 12). The defaults
/// are the engine's own: ten messages or one second, one batch at a time.
/// </summary>
public sealed class GoldpathBatchOptions
{
    /// <summary>The most messages one batch holds. Positive; 10 by default.</summary>
    public int MessageLimit { get; set; } = 10;

    /// <summary>How long a partial batch waits for more before it is delivered. Positive; one second by default.</summary>
    public TimeSpan TimeLimit { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many batches one endpoint handles at once. Positive; 1 by default, which serialises the handler.</summary>
    public int ConcurrencyLimit { get; set; } = 1;

    /// <summary>Refuses, in words, a grouping no engine can honour.</summary>
    internal void Validate(Type handler)
    {
        if (MessageLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MessageLimit), MessageLimit, $"The batch handler '{handler.Name}' needs a MessageLimit of at least 1.");
        }

        if (TimeLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TimeLimit), TimeLimit, $"The batch handler '{handler.Name}' needs a positive TimeLimit.");
        }

        if (ConcurrencyLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrencyLimit), ConcurrencyLimit, $"The batch handler '{handler.Name}' needs a ConcurrencyLimit of at least 1.");
        }
    }
}

/// <summary>
/// The MassTransit side of the batch seam: one library batch consumer per (event, handler) pair, resolving the
/// adopter's handler from the batch scope. Internal: an adopter names the handler, never this type.
/// </summary>
internal sealed class GoldpathIntegrationEventBatchConsumer<TEvent, THandler>(THandler handler) : IConsumer<Batch<TEvent>>
    where TEvent : class, IIntegrationEvent
    where THandler : class, IIntegrationEventBatchHandler<TEvent>
{
    public Task Consume(ConsumeContext<Batch<TEvent>> context)
    {
        var attempt = context.GetRetryAttempt();
        var batch = new List<IntegrationEvent<TEvent>>(context.Message.Length);
        foreach (var delivery in context.Message)
        {
            // Each message keeps its own tenant: one batch may carry several, and the batch scope's
            // single tenant holder cannot say which one a given message came from.
            TenantId? tenant = delivery.Headers.Get<string>(GoldpathHeaders.TenantId) is { } raw && TenantId.TryCreate(raw, out var parsed)
                ? parsed
                : null;
            batch.Add(new IntegrationEvent<TEvent>(
                delivery.Message,
                new IntegrationEventContext(
                    delivery.MessageId,
                    delivery.Headers.Get<string>(GoldpathHeaders.CorrelationId),
                    tenant,
                    attempt,
                    IntegrationEventHeaders.Of(delivery))));
        }

        return handler.HandleAsync(batch, context.CancellationToken);
    }
}
