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
    private readonly Dictionary<(Type Event, Type Handler), string> _endpointNames = [];
    private readonly List<(Type Event, Type Handler, GoldpathBatchOptions Options, string EndpointName)> _batchHandlers = [];

    internal GoldpathMessagingBuilder(GoldpathMessagingOptions options) => Options = options;

    /// <summary>The message-path tuning, already bound from <c>Goldpath:Messaging</c>; changes here win.</summary>
    public GoldpathMessagingOptions Options { get; }

    internal IReadOnlyList<(Type Event, Type Handler)> Handlers => _handlers;

    internal IReadOnlyList<(Type Event, Type Handler, GoldpathBatchOptions Options, string EndpointName)> BatchHandlers => _batchHandlers;

    internal Type? OutboxContext { get; private set; }

    internal GoldpathOutboxOptions? Outbox { get; private set; }

    internal GoldpathMessagingTransport Transport { get; private set; }

    internal string RabbitMqConnectionName { get; private set; } = "";

    /// <summary>
    /// The engine to run on when several are referenced, by name: only the conformance suite sets it, to run
    /// its facts once per engine. An application never does; it references one adapter.
    /// </summary>
    internal string? EngineName { get; set; }

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
    /// Registers <typeparamref name="THandler"/> for <typeparamref name="TEvent"/> on the endpoint named
    /// <paramref name="endpointName"/> rather than the one <see cref="GoldpathHandlerEndpoints.NameOf"/> derives.
    /// For a queue that must keep a name it already has on a running fleet (a mixed fleet drains one queue):
    /// handlers given the same name share one queue.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pair is already registered on another endpoint.</exception>
    public GoldpathMessagingBuilder AddHandler<TEvent, THandler>(string endpointName)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        var pair = (typeof(TEvent), typeof(THandler));
        var current = _endpointNames.TryGetValue(pair, out var named) ? named : _handlers.Contains(pair) ? GoldpathHandlerEndpoints.NameOf(typeof(THandler)) : null;
        if (current is not null && current != endpointName)
        {
            throw new InvalidOperationException(
                $"'{typeof(THandler).Name}' already handles '{typeof(TEvent).Name}' on the endpoint '{current}'; one handler drains one queue, not a second named '{endpointName}'.");
        }

        _endpointNames[pair] = endpointName;
        return AddHandler<TEvent, THandler>();
    }

    /// <summary>
    /// Registers <typeparamref name="THandler"/> to take <typeparamref name="TEvent"/> in batches grouped by
    /// <paramref name="options"/> (RFC goldpath-messaging-engines §4.1 row 12). Its queue is named by
    /// <see cref="GoldpathHandlerEndpoints.NameOf"/> unless <paramref name="endpointName"/> names it; handlers
    /// given the same name share one queue.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit in <paramref name="options"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The pair is already registered as a batch handler.</exception>
    public GoldpathMessagingBuilder AddBatchHandler<TEvent, THandler>(GoldpathBatchOptions options, string? endpointName = null)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventBatchHandler<TEvent>
    {
        ArgumentNullException.ThrowIfNull(options);
        if (endpointName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);
        }

        options.Validate(typeof(THandler));
        if (_batchHandlers.Exists(b => b.Event == typeof(TEvent) && b.Handler == typeof(THandler)))
        {
            throw new InvalidOperationException(
                $"'{typeof(THandler).Name}' is already registered as the batch handler of '{typeof(TEvent).Name}'.");
        }

        // A copy: the grouping is fixed at registration, so a caller changing the object later changes nothing.
        var grouping = new GoldpathBatchOptions
        {
            MessageLimit = options.MessageLimit,
            TimeLimit = options.TimeLimit,
            ConcurrencyLimit = options.ConcurrencyLimit,
        };
        _batchHandlers.Add((typeof(TEvent), typeof(THandler), grouping, endpointName ?? GoldpathHandlerEndpoints.NameOf(typeof(THandler))));
        return this;
    }

    /// <summary>The endpoint a registered single-message handler drains.</summary>
    internal string EndpointOf(Type eventType, Type handlerType)
        => _endpointNames.TryGetValue((eventType, handlerType), out var name) ? name : GoldpathHandlerEndpoints.NameOf(handlerType);

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
