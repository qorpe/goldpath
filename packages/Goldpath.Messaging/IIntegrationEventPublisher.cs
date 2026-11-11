using MassTransit;

namespace Goldpath;

/// <summary>
/// The publish seam: how an application emits an integration event, expressed in Goldpath's
/// own vocabulary instead of the transport library's.
///
/// <para>WHY this exists, written down so it is never mistaken for ceremony: a command
/// handler is the code an adopter writes most, and until now the template put
/// <c>IPublishEndpoint</c> — a MassTransit type — straight into its constructor. That made
/// the library part of every adopter's own signature, so a future transport change (the
/// 8.x maintenance window is finite; see
/// <c>docs/rfc/goldpath-messaging-exit.md</c>) would have edited THEIR code, not just ours.
/// With zero adopters today, moving that line costs nothing; with N adopters it costs N
/// migrations.</para>
///
/// <para>What this is NOT: a message-bus abstraction. ADR-0003 forbids rewriting what the
/// ecosystem provides, and the implementation is a one-line delegation to
/// <c>IPublishEndpoint</c> — no retry policy, no topology, no routing decisions of our own.
/// The CONSUME side deliberately stays on the library's types: a consumer genuinely uses
/// its pipeline (headers, retry, redelivery, tenant restore), and wrapping that would be a
/// leaky abstraction that costs more than the migration it saves.</para>
///
/// <para>The honest promise to an adopter: <b>if the transport ever changes, your command
/// handlers do not move; your consumers do.</b></para>
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Publishes an integration event to every interested service. With
    /// <c>features.outbox</c> enabled the publish joins the ambient transaction, so the
    /// event and the state change commit together or not at all.
    /// </summary>
    /// <typeparam name="TEvent">The event type — must carry the <see cref="IIntegrationEvent"/>
    /// marker, which the GP0401 analyzer enforces at build time.</typeparam>
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent;
}

/// <summary>
/// The MassTransit-backed publisher. Deliberately thin: it forwards, it does not decide.
/// Everything Goldpath adds to the publish path — the integration-event guard, the
/// tenant/correlation propagation — already lives in the pipeline filters, where it applies
/// to every publish regardless of who called it.
/// </summary>
internal sealed class MassTransitIntegrationEventPublisher(IPublishEndpoint endpoint, MassTransitPublishMode? mode = null)
    : IIntegrationEventPublisher, IGoldpathBatchPublisher
{
    public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
        => endpoint.Publish(integrationEvent, cancellationToken);

    /// <summary>
    /// The batch (#285): concurrent on the wire only when the engine composed NO outbox. With the bus outbox the scoped
    /// <c>IPublishEndpoint</c> adds <c>OutboxMessage</c> rows to the scope's <c>DbContext</c>, which cannot be touched
    /// concurrently; and a registration that did not come through the engine (the <c>configureBus</c> overload, where an
    /// adopter may call <c>UseBusOutbox</c> themselves) carries no mode, so it is never assumed outbox-free.
    /// </summary>
    public Task PublishBatchAsync<TEvent>(IReadOnlyList<TEvent> integrationEvents, CancellationToken cancellationToken)
        where TEvent : class, IIntegrationEvent
        => mode is { ConcurrentOnTheWire: true }
            ? GoldpathBatchPublishing.BoundedAsync(integrationEvents, (e, ct) => endpoint.Publish(e, ct), cancellationToken)
            : GoldpathBatchPublishing.OneAtATimeAsync(this, integrationEvents, cancellationToken);
}

/// <summary>
/// What the MassTransit engine composed, as the batch publish needs to know it (#285): <see cref="ConcurrentOnTheWire"/>
/// is true only when no outbox is composed, so a publish goes straight to the broker.
/// </summary>
internal sealed record MassTransitPublishMode(bool ConcurrentOnTheWire);
