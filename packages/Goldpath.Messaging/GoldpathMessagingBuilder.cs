using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Goldpath;

/// <summary>
/// The registration vocabulary of the message path, in Goldpath's own types (ADR-0013 decision 1,
/// RFC goldpath-messaging-engines §4.1 row 5): what an application declares — its handlers, its
/// outbox, its transport — and nothing about which engine carries it. The engine composes what this
/// builder recorded; switching engines changes a package reference, never this block.
/// </summary>
public sealed class GoldpathMessagingBuilder
{
    private readonly List<(Type Event, Type Handler)> _handlers = [];

    internal GoldpathMessagingBuilder(GoldpathMessagingOptions options) => Options = options;

    /// <summary>The message-path tuning, already bound from <c>Goldpath:Messaging</c>; changes here win.</summary>
    public GoldpathMessagingOptions Options { get; }

    internal IReadOnlyList<(Type Event, Type Handler)> Handlers => _handlers;

    internal Type? OutboxContext { get; private set; }

    internal GoldpathOutboxOptions? Outbox { get; private set; }

    internal GoldpathMessagingTransport Transport { get; private set; }

    internal string RabbitMqConnectionName { get; private set; } = "";

    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <typeparamref name="TEvent"/>. Its queue is named
    /// by <see cref="GoldpathHandlerEndpoints.NameOf"/>, exactly as the MassTransit-typed registration
    /// names it, so moving to this builder moves no queue.
    /// </summary>
    public GoldpathMessagingBuilder AddHandler<TEvent, THandler>()
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        if (!_handlers.Contains((typeof(TEvent), typeof(THandler))))
        {
            _handlers.Add((typeof(TEvent), typeof(THandler)));
        }

        return this;
    }

    /// <summary>
    /// Registers every <see cref="IIntegrationEventHandler{TEvent}"/> implementation found in
    /// <paramref name="assemblies"/>; a handler implementing several event interfaces joins once per event.
    /// An open generic type is skipped: it cannot be an endpoint, and a closed type deriving from it is found.
    /// </summary>
    public GoldpathMessagingBuilder AddHandlers(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var open = typeof(IIntegrationEventHandler<>);
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }))
            {
                foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == open))
                {
                    var pair = (contract.GetGenericArguments()[0], type);
                    if (!_handlers.Contains(pair))
                    {
                        _handlers.Add(pair);
                    }
                }
            }
        }

        return this;
    }

    /// <summary>
    /// Composes the transactional outbox and the consumer-side inbox on <typeparamref name="TContext"/>:
    /// a publish commits with the business data, and a redelivered message is handled once. The row
    /// lock statements follow the context's own EF provider (PostgreSQL or SQL Server), so the provider
    /// choice stays where the manifest already made it.
    /// </summary>
    /// <exception cref="InvalidOperationException">An outbox was already composed on another context.</exception>
    public GoldpathMessagingBuilder AddOutbox<TContext>(Action<GoldpathOutboxOptions>? configure = null)
        where TContext : DbContext
    {
        if (OutboxContext is { } existing && existing != typeof(TContext))
        {
            throw new InvalidOperationException(
                $"The outbox is already composed on '{existing.Name}'; one message path has one outbox, not a second on '{typeof(TContext).Name}'.");
        }

        var options = Outbox ?? new GoldpathOutboxOptions();
        configure?.Invoke(options);
        OutboxContext = typeof(TContext);
        Outbox = options;
        return this;
    }

    /// <summary>
    /// Carries the message path over RabbitMQ, at the connection string named
    /// <paramref name="connectionName"/> (<c>ConnectionStrings:messaging</c> by default, which is the
    /// name the AppHost gives the broker). With no such connection string, the engine's local default
    /// applies, as the template's block does today.
    /// </summary>
    public GoldpathMessagingBuilder UseRabbitMq(string connectionName = "messaging")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        SetTransport(GoldpathMessagingTransport.RabbitMq);
        RabbitMqConnectionName = connectionName;
        return this;
    }

    /// <summary>The in-process transport: what <see cref="GoldpathMessagingTestHost"/> runs on unless told otherwise.</summary>
    internal GoldpathMessagingBuilder UseInMemory()
    {
        SetTransport(GoldpathMessagingTransport.InMemory);
        return this;
    }

    private void SetTransport(GoldpathMessagingTransport transport)
    {
        if (Transport != GoldpathMessagingTransport.None && Transport != transport)
        {
            throw new InvalidOperationException(
                $"The message path already runs on {Transport}; one message path has one transport, not a second on {transport}.");
        }

        Transport = transport;
    }
}

/// <summary>
/// The outbox and inbox, in Goldpath's terms (RFC goldpath-messaging-engines §4.1 row 6). The tables
/// behind them are the ones every adopter's migrations already hold (§4.4).
/// </summary>
public sealed class GoldpathOutboxOptions
{
    /// <summary>How long a delivered message id is remembered for deduplication. 30 minutes, as before.</summary>
    public TimeSpan DuplicateDetectionWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Whether every receive endpoint deduplicates against the inbox, so a redelivered message is handled
    /// once. On by default, as before; turning it off leaves only the publish side outboxed.
    /// </summary>
    public bool Inbox { get; set; } = true;

    /// <summary>
    /// How often the delivery loop looks for committed, undelivered messages; null keeps the engine's
    /// own default, which is what an application composed before this option existed runs on.
    /// </summary>
    public TimeSpan? DeliveryPollInterval { get; set; }
}

/// <summary>Which transport a builder selected.</summary>
internal enum GoldpathMessagingTransport
{
    None,
    RabbitMq,
    InMemory,
}
