using System.Diagnostics;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

[assembly: Goldpath.GoldpathMessagingEngine(typeof(Goldpath.WolverineMessagingEngine))]

namespace Goldpath;

/// <summary>
/// The message path on Wolverine (G5, RFC goldpath-messaging-engines §2): Wolverine receives — a listener per
/// endpoint reading the 8.x envelope, the retry ladder, and the move to <c>&lt;queue&gt;_error</c> — and the
/// adapter writes the wire and the tables every adapter shares (§4.4). Not selectable yet (ADR-0015 §2):
/// <c>wolverine</c> enters the manifest enum with its S1-S4 and S6 proofs (open-threads T18).
/// </summary>
internal sealed class WolverineMessagingEngine : IGoldpathMessagingEngine
{
    /// <inheritdoc />
    public string Name => "wolverine";

    /// <inheritdoc />
    public void Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(composition);
        if (composition.Transport != GoldpathMessagingTransport.RabbitMq)
        {
            throw new InvalidOperationException(
                $"The Wolverine adapter runs on RabbitMQ; the message path asks for {composition.Transport} (open-threads T18).");
        }

        // The default engine's delayed redelivery rides the broker's scheduler and survives a restart. A delay this
        // adapter could only hold in process memory would lose the message with the process, so it is refused.
        if (composition.Options.Retry.RedeliveryIntervals.Count > 0)
        {
            throw new InvalidOperationException(
                "The Wolverine adapter has no durable delayed redelivery yet (open-threads T18). Clear " +
                "Goldpath:Messaging:Retry:RedeliveryIntervals, so a message that exhausts its immediate retries goes " +
                "to its _error queue, or run on the default engine.");
        }

        if (composition.Options.Retry.ImmediateCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GoldpathMessagingOptions.RetryOptions.ImmediateCount),
                composition.Options.Retry.ImmediateCount, "Immediate retries cannot be negative.");
        }

        var broker = GoldpathWire.BrokerOf(composition, configuration);
        var state = new WolverineEngineState(composition.OutboxContext, composition.Outbox);

        // Built by the container, so the container disposes its connection with the host.
        services.AddSingleton(_ => new GoldpathWire(broker));
        services.AddSingleton(state);
        services.TryAddScoped<IIntegrationEventPublisher, WolverineIntegrationEventPublisher>();

        var endpoints = composition.Handlers
            .GroupBy(h => h.EndpointName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var batchEndpoints = composition.BatchHandlers
            .GroupBy(b => b.EndpointName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        if (endpoints.Keys.Intersect(batchEndpoints.Keys, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            throw new InvalidOperationException(
                $"The Wolverine adapter drains '{both}' either one message at a time or in batches, not both (open-threads T18).");
        }

        // Declared before Wolverine starts its listeners (hosted services start in registration order).
        var topology = endpoints.ToDictionary(e => e.Key, e => e.Value.Select(h => h.Event).Distinct().ToList(), StringComparer.Ordinal);
        foreach (var (endpoint, batches) in batchEndpoints)
        {
            topology[endpoint] = [.. batches.Select(b => b.Event).Distinct()];
        }

        services.AddHostedService(sp => new WolverineTopology(sp.GetRequiredService<GoldpathWire>(), topology));

        foreach (var handler in composition.Handlers.Select(h => h.Handler).Concat(composition.BatchHandlers.Select(b => b.Handler)))
        {
            services.TryAddScoped(handler);
        }

        if (composition.OutboxContext is { } context)
        {
            ValidateOutbox(composition.Outbox!);
            services.AddSingleton(composition.Outbox!);
            services.AddSingleton(typeof(IHostedService), typeof(WolverineOutboxProviderCheck<>).MakeGenericType(context));
            services.AddSingleton(typeof(IHostedService), typeof(WolverineOutboxRelay<>).MakeGenericType(context));
        }

        var retries = composition.Options.Retry.ImmediateCount;
        services.AddWolverine(options =>
        {
            options.ServiceName = "goldpath";
            options.Discovery.DisableConventionalDiscovery();

            // Every handler is a hand-written sticky handler: no code is generated, so no runtime compiler ships.
            options.CodeGeneration.TypeLoadMode = JasperFx.CodeGeneration.TypeLoadMode.Static;
            options.UseRabbitMq(broker).DisableSystemRequestReplyQueueDeclaration();
            options.Policies.OnAnyException().RetryTimes(retries).Then.MoveToErrorQueue();

            foreach (var (endpoint, registrations) in endpoints)
            {
                // Wolverine's interop reads the 8.x envelope but does not resolve its messageType URN to a
                // registered type, so an endpoint names the one event it drains (open-threads T18: an endpoint
                // shared by several event types is refused until the URN is resolved here).
                var events = registrations.Select(r => r.Event).Distinct().ToList();
                if (events.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"The Wolverine adapter drains one event type per endpoint; '{endpoint}' takes " +
                        string.Join(", ", events.Select(e => e.Name)) + " (open-threads T18).");
                }

                var listener = options.ListenToRabbitQueue(endpoint)
                    .UseMassTransitInterop()
                    .DefaultIncomingMessage(events[0])
                    .DeadLetterQueueing(new DeadLetterQueue($"{endpoint}_error", DeadLetterQueueMode.InteropFriendly))
                    .ProcessInline();
                foreach (var byEvent in registrations.GroupBy(r => r.Event))
                {
                    options.RegisterMessageType(byEvent.Key);
                    listener.AddStickyHandler(byEvent.Key, SeamHandler.For(byEvent.Key, [.. byEvent.Select(r => r.Handler)], endpoint, state));
                }
            }

            foreach (var (endpoint, batches) in batchEndpoints)
            {
                if (batches.Count > 1)
                {
                    throw new InvalidOperationException(
                        $"The Wolverine adapter runs one batch handler per endpoint; '{endpoint}' has " +
                        string.Join(", ", batches.Select(b => b.Handler.Name)) + " (open-threads T18).");
                }

                // A batch is gathered from deliveries the listener holds in flight together and acknowledges
                // only once their batch has run, so the window is the batch's own size.
                var batch = batches[0];
                options.RegisterMessageType(batch.Event);
                options.ListenToRabbitQueue(endpoint)
                    .UseMassTransitInterop()
                    .DefaultIncomingMessage(batch.Event)
                    .DeadLetterQueueing(new DeadLetterQueue($"{endpoint}_error", DeadLetterQueueMode.InteropFriendly))
                    .PreFetchCount((ushort)Math.Min(batch.Options.MessageLimit * (long)batch.Options.ConcurrencyLimit, ushort.MaxValue))
                    .ProcessInParallelWithNativeAcks()
                    .MaximumParallelMessages(batch.Options.MessageLimit * batch.Options.ConcurrencyLimit)
                    .AddStickyHandler(batch.Event, BatchSeamHandler.For(batch, state));
            }
        });
    }

    /// <inheritdoc />
    public Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(message);
        var wire = services.GetRequiredService<GoldpathWire>();
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (message.Tenant is { } tenant)
        {
            headers[GoldpathHeaders.TenantId] = tenant;
        }

        if (message.CorrelationId is { } correlation)
        {
            headers[GoldpathHeaders.CorrelationId] = correlation;
        }

        foreach (var (key, value) in message.Headers)
        {
            headers[key] = value?.ToString() ?? "";
        }

        var messageId = message.MessageId ?? Guid.NewGuid();
        return wire.PublishAsync(GoldpathWire.ExchangeOf(contract), messageId, wire.Envelope(contract, body, messageId, headers, DateTimeOffset.UtcNow), cancellationToken);
    }

    private static void ValidateOutbox(GoldpathOutboxOptions outbox)
    {
        if (outbox.DuplicateDetectionWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GoldpathOutboxOptions.DuplicateDetectionWindow), outbox.DuplicateDetectionWindow,
                "The outbox's duplicate-detection window must be positive.");
        }

        if (outbox.DeliveryPollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(GoldpathOutboxOptions.DeliveryPollInterval), interval,
                "The outbox's delivery poll interval must be positive, or null for the engine's default.");
        }
    }
}

/// <summary>What the adapter's parts share about the composition.</summary>
internal sealed record WolverineEngineState(Type? OutboxContext, GoldpathOutboxOptions? Outbox);

/// <summary>Declares every endpoint's 8.x topology before Wolverine's listeners start.</summary>
internal sealed class WolverineTopology(GoldpathWire wire, IReadOnlyDictionary<string, List<Type>> endpoints) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (endpoint, events) in endpoints)
        {
            await wire.DeclareEndpointAsync(endpoint, events, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// One endpoint's handlers of one event, as Wolverine's handler (a sticky handler, so no code is generated): the
/// tenant restored from <c>X-Goldpath-Tenant</c> into the message scope and the ambient tenant, the correlation
/// tagged on the activity, the inbox consulted on the application's context, and each seam handler called with
/// its <see cref="IntegrationEventContext"/>. A throw leaves to Wolverine's retry ladder.
/// </summary>
internal abstract class SeamHandler : IMessageHandler
{
    public abstract Type MessageType { get; }

    public LogLevel SuccessLogLevel => LogLevel.Debug;

    public LogLevel ProcessingLogLevel => LogLevel.Debug;

    public LogLevel FailureLogLevel => LogLevel.Error;

    public bool TelemetryEnabled => true;

    public static SeamHandler For(Type eventType, Type[] handlers, string endpoint, WolverineEngineState state)
        => (SeamHandler)Activator.CreateInstance(typeof(SeamHandler<>).MakeGenericType(eventType), handlers, endpoint, state)!;

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    /// <summary>What a delivery carries for the seam: its headers, its tenant when valid, its correlation.</summary>
    protected static (Dictionary<string, object?> Headers, TenantId? Tenant, string? CorrelationId) Read(Envelope envelope)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, header) in envelope.Headers)
        {
            headers[key] = header;
        }

        TenantId? tenant = headers.TryGetValue(GoldpathHeaders.TenantId, out var raw) && raw is string value && TenantId.TryCreate(value, out var parsed)
            ? parsed
            : null;
        var correlationId = headers.TryGetValue(GoldpathHeaders.CorrelationId, out var correlation) ? correlation as string : null;
        return (headers, tenant, correlationId);
    }

    /// <summary>
    /// Commits what a handler published when nothing else saves the scope's context (no inbox, or a batch): with an
    /// outbox composed, a publish only adds <c>OutboxMessage</c> rows, which the scope would otherwise discard.
    /// </summary>
    protected static async Task CommitPublishedAsync(IServiceProvider services, WolverineEngineState state, CancellationToken cancellation)
    {
        if (state.OutboxContext is { } outboxContext
            && (DbContext)services.GetRequiredService(outboxContext) is var db
            && db.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
        {
            await db.SaveChangesAsync(cancellation);
        }
    }

    public void RecordCauseAndEffect(MessageContext context, Wolverine.Runtime.Agents.IWolverineObserver observer)
    {
    }
}

internal sealed class SeamHandler<TEvent>(Type[] handlers, string endpoint, WolverineEngineState state) : SeamHandler
    where TEvent : class, IIntegrationEvent
{
    // The inbox's consumer key per handler, the one the default engine writes (GoldpathInboxKey): a delivery one
    // engine of a mixed fleet recorded is recognised by the other, handler by handler.
    private readonly (Type Handler, Guid ConsumerId)[] _consumers =
        [.. handlers.Select(handler => (handler, GoldpathInboxKey.ConsumerIdOf(typeof(TEvent), handler, endpoint)))];

    public override Type MessageType => typeof(TEvent);

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope ?? throw new InvalidOperationException("A message arrived without its envelope.");
        var message = envelope.Message as TEvent ?? throw new InvalidOperationException(
            $"The message on '{endpoint}' is not a '{typeof(TEvent).Name}'.");
        var (headers, tenant, correlationId) = Read(envelope);
        using var scope = context.Runtime.Services.CreateScope();
        var tenantHolder = scope.ServiceProvider.GetRequiredService<GoldpathMessageTenantContext>();
        var previousAmbient = GoldpathAmbientTenant.Current;
        if (tenant is { } restored)
        {
            tenantHolder.Current = restored;
            GoldpathAmbientTenant.Current = restored;
        }

        if (correlationId is not null)
        {
            Activity.Current?.SetTag("goldpath.correlation_id", correlationId);
        }

        try
        {
            var eventContext = new IntegrationEventContext(envelope.Id, correlationId, tenantHolder.Current, Math.Max(0, envelope.Attempts - 1), headers);
            if (state.OutboxContext is { } contextType && state.Outbox is { Inbox: true } outbox)
            {
                await HandleOnceAsync(scope.ServiceProvider, (DbContext)scope.ServiceProvider.GetRequiredService(contextType), outbox, message, eventContext, cancellation);
            }
            else
            {
                await InvokeAsync(scope.ServiceProvider, message, eventContext, cancellation);
                await CommitPublishedAsync(scope.ServiceProvider, state, cancellation);
            }
        }
        finally
        {
            GoldpathAmbientTenant.Current = previousAmbient;
        }
    }

    /// <summary>
    /// The inbox: each handler's delivery is recorded in <c>InboxState</c> keyed by (message id, consumer) in the
    /// same transaction as the handlers' own writes and their outbox rows, so a second delivery of a consumed
    /// message is acknowledged without running a handler again. One row per handler, as the default engine keeps
    /// them, so a handler that already ran under either engine is skipped and only the others run.
    /// </summary>
    private async Task HandleOnceAsync(IServiceProvider services, DbContext db, GoldpathOutboxOptions outbox, TEvent message, IntegrationEventContext eventContext, CancellationToken cancellation)
    {
        var messageId = eventContext.MessageId ?? throw new InvalidOperationException("The inbox needs a message id.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellation);
        var lockSql = InboxLockSql(db);
        var ran = false;
        foreach (var (handlerType, consumerId) in _consumers)
        {
            var seen = await db.Set<InboxState>()
                .FromSqlRaw(lockSql, messageId, consumerId)
                .AsTracking()
                .FirstOrDefaultAsync(cancellation);
            if (seen?.Consumed is not null)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            if (seen is null)
            {
                seen = new InboxState { MessageId = messageId, ConsumerId = consumerId, LockId = Guid.NewGuid(), Received = now, ReceiveCount = 0 };
                db.Set<InboxState>().Add(seen);
            }

            seen.ReceiveCount++;
            await InvokeAsync(services, handlerType, message, eventContext, cancellation);
            seen.Consumed = DateTime.UtcNow;
            seen.Delivered = seen.Consumed;
            seen.ExpirationTime = seen.Consumed + outbox.DuplicateDetectionWindow;
            ran = true;
        }

        if (!ran)
        {
            await transaction.RollbackAsync(cancellation);
            return;
        }

        await db.SaveChangesAsync(cancellation);
        await transaction.CommitAsync(cancellation);
    }

    private async Task InvokeAsync(IServiceProvider services, TEvent message, IntegrationEventContext eventContext, CancellationToken cancellation)
    {
        foreach (var handlerType in handlers)
        {
            await InvokeAsync(services, handlerType, message, eventContext, cancellation);
        }
    }

    private static Task InvokeAsync(IServiceProvider services, Type handlerType, TEvent message, IntegrationEventContext eventContext, CancellationToken cancellation)
        => ((IIntegrationEventHandler<TEvent>)services.GetRequiredService(handlerType)).HandleAsync(message, eventContext, cancellation);

    private static string InboxLockSql(DbContext db)
    {
        var entity = db.Model.FindEntityType(typeof(InboxState))
            ?? throw new InvalidOperationException($"'{db.GetType().Name}' maps no inbox table; call modelBuilder.AddGoldpathOutboxEntities().");
        var table = entity.GetSchema() is { } schema ? $"\"{schema}\".\"{entity.GetTableName()}\"" : $"\"{entity.GetTableName()}\"";
        var messageId = entity.FindProperty(nameof(InboxState.MessageId))!.GetColumnName();
        var consumerId = entity.FindProperty(nameof(InboxState.ConsumerId))!.GetColumnName();
        return $"SELECT * FROM {table} WHERE \"{messageId}\" = {{0}} AND \"{consumerId}\" = {{1}} FOR UPDATE";
    }
}
