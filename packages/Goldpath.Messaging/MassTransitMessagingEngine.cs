using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// The MassTransit 8.x engine behind <see cref="GoldpathMessagingBuilder"/>: it turns what the builder
/// recorded into the same registrations the MassTransit-typed overloads make, so both vocabularies
/// produce one bus — the same queues, the same pipeline, the same tables. Internal until the adapter
/// package is extracted (RFC goldpath-messaging-engines, Part 4/4).
/// </summary>
internal sealed class MassTransitMessagingEngine : IGoldpathMessagingEngine
{
    private static readonly System.Reflection.MethodInfo s_addHandler =
        typeof(GoldpathHandlerRegistrationExtensions).GetMethod(nameof(GoldpathHandlerRegistrationExtensions.AddHandlerAt), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static readonly System.Reflection.MethodInfo s_addBatchHandler =
        typeof(MassTransitMessagingEngine).GetMethod(nameof(AddBatchHandler), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static readonly System.Reflection.MethodInfo s_composeOutbox =
        typeof(MassTransitMessagingEngine).GetMethod(nameof(ComposeOutbox), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    /// <inheritdoc />
    public string Name => "masstransit";

    /// <inheritdoc />
    void IGoldpathMessagingEngine.Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration)
    {
        Validate(composition.Builder, services);

        // The publish seam (RFC goldpath-messaging-exit §5), scoped because the outbox's publish endpoint is.
        services.TryAddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();
        services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            Compose(composition.Builder, bus, configuration);
        });

        if (composition.WaitUntilStarted)
        {
            services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
        }
    }

    /// <inheritdoc />
    public Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(message);
        var bus = services.GetRequiredService<IBus>();

        // An instance of the contract publishes as itself; any other body is materialised under the
        // contract property by property — the wider or older shape an upstream producer sends.
        var publish = contract.IsInstanceOfType(body) ? s_publishMessage : s_publishValues;
        return (Task)publish.MakeGenericMethod(contract).Invoke(null, [bus, body, message, cancellationToken])!;
    }

    private static readonly System.Reflection.MethodInfo s_publishMessage =
        typeof(MassTransitMessagingEngine).GetMethod(nameof(PublishMessage), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static readonly System.Reflection.MethodInfo s_publishValues =
        typeof(MassTransitMessagingEngine).GetMethod(nameof(PublishValues), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static Task PublishMessage<TEvent>(IBus bus, object message, GoldpathOutgoingMessage outgoing, CancellationToken cancellationToken)
        where TEvent : class
        => bus.Publish((TEvent)message, context => Apply(outgoing, context), cancellationToken);

    private static Task PublishValues<TEvent>(IBus bus, object values, GoldpathOutgoingMessage outgoing, CancellationToken cancellationToken)
        where TEvent : class
        => bus.Publish<TEvent>(values, context => Apply(outgoing, context), cancellationToken);

    private static void Apply(GoldpathOutgoingMessage outgoing, PublishContext context)
    {
        if (outgoing.MessageId is { } messageId)
        {
            context.MessageId = messageId;
        }

        if (outgoing.Tenant is { } tenant)
        {
            context.Headers.Set(GoldpathHeaders.TenantId, tenant);
        }

        if (outgoing.CorrelationId is { } correlationId)
        {
            context.Headers.Set(GoldpathHeaders.CorrelationId, correlationId);
        }

        foreach (var (key, value) in outgoing.Headers)
        {
            context.Headers.Set(key, value);
        }
    }

    /// <summary>
    /// Refuses, at registration, what would otherwise fail only once the bus runs: an outbox interval
    /// that is not positive, and — when the host starts, before any message is delivered — an outbox
    /// on a context whose EF provider has no lock statements.
    /// </summary>
    public static void Validate(GoldpathMessagingBuilder messaging, IServiceCollection services)
    {
        if (messaging.Outbox is not { } outbox || messaging.OutboxContext is not { } context)
        {
            return;
        }

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

        services.AddSingleton(typeof(IHostedService), typeof(OutboxProviderCheck<>).MakeGenericType(context));
    }

    /// <summary>Composes <paramref name="messaging"/> onto MassTransit's registration.</summary>
    public static void Compose(GoldpathMessagingBuilder messaging, IBusRegistrationConfigurator bus, IConfiguration configuration)
    {
        foreach (var (eventType, handlerType) in messaging.Handlers)
        {
            var endpoint = messaging.EndpointOf(eventType, handlerType);
            s_addHandler.MakeGenericMethod(eventType, handlerType).Invoke(null, [bus, endpoint, messaging.ConcurrencyLimitOf(endpoint)]);
        }

        // The event types this bus handles: the ones a delivery's envelope must match by wire name (§4.1 row 15).
        var handled = messaging.Handlers.Select(h => h.Event).Concat(messaging.BatchHandlers.Select(b => b.Event)).ToList();

        foreach (var (eventType, handlerType, grouping, endpointName) in messaging.BatchHandlers)
        {
            s_addBatchHandler.MakeGenericMethod(eventType, handlerType).Invoke(null, [bus, grouping, endpointName]);
        }

        if (TestHooks.TryGetValue(messaging, out var hooks))
        {
            hooks.Registration?.Invoke(bus);
        }

        if (messaging.OutboxContext is { } context)
        {
            s_composeOutbox.MakeGenericMethod(context).Invoke(null, [bus, messaging.Outbox!]);
        }

        switch (messaging.Transport)
        {
            case GoldpathMessagingTransport.RabbitMq:
                bus.UsingRabbitMq((registration, cfg) =>
                {
                    // Absent means the engine's local default; present and blank is a mistake, refused in words.
                    if (configuration.GetConnectionString(messaging.RabbitMqConnectionName) is { } connection)
                    {
                        if (string.IsNullOrWhiteSpace(connection))
                        {
                            throw new InvalidOperationException(
                                $"ConnectionStrings:{messaging.RabbitMqConnectionName} is present but empty. Set the broker's " +
                                "address, or remove the entry to use the engine's local default.");
                        }

                        cfg.Host(new Uri(connection));
                    }

                    if (TestHooks.TryGetValue(messaging, out var transportHooks))
                    {
                        transportHooks.Transport?.Invoke(cfg);
                    }

                    MassTransitMessageNames.Apply(cfg, handled);
                    cfg.ConfigureGoldpathEndpoints(registration);
                });
                break;
            case GoldpathMessagingTransport.InMemory:
                // The in-memory transport routes by the CLR type and is left as it was: a wire name is a broker's
                // concern (open-threads T53), proven on RabbitMQ by the conformance suite's S2.17.
                bus.UsingInMemory((registration, cfg) => cfg.ConfigureGoldpathEndpoints(registration));
                break;
            default:
                throw new InvalidOperationException($"No transport composition for {messaging.Transport}.");
        }
    }

    /// <summary>
    /// Engine tuning reached only by Goldpath's own tests (the integration suites size a consumer's slots and a
    /// bench's prefetch), until the adapter's public escape hatch lands (RFC §9 Q3, open-threads T44). Keyed by
    /// builder, so two hosts in one process never see each other's.
    /// </summary>
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GoldpathMessagingBuilder, EngineTestHooks> TestHooks = [];

    /// <summary>What <see cref="TestHooks"/> holds: a registration callback and a RabbitMQ transport callback.</summary>
    internal sealed record EngineTestHooks(Action<IBusRegistrationConfigurator>? Registration, Action<IBusFactoryConfigurator>? Transport);

    /// <summary>Registers a batch handler: its scope, and one batch consumer grouped as the options say.</summary>
    private static void AddBatchHandler<TEvent, THandler>(IBusRegistrationConfigurator bus, GoldpathBatchOptions grouping, string endpointName)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventBatchHandler<TEvent>
    {
        bus.TryAddScoped<THandler>();
        bus.AddConsumer<GoldpathIntegrationEventBatchConsumer<TEvent, THandler>>((_, consumer) => consumer.Options<BatchOptions>(batch => batch
                .SetMessageLimit(grouping.MessageLimit)
                .SetTimeLimit(grouping.TimeLimit)
                .SetConcurrencyLimit(grouping.ConcurrencyLimit)))
            .Endpoint(endpoint => endpoint.Name = endpointName);
    }

    private static void ComposeOutbox<TContext>(IBusRegistrationConfigurator bus, GoldpathOutboxOptions options)
        where TContext : DbContext
        => GoldpathMessagingExtensions.ComposeOutbox<TContext>(bus, outbox =>
        {
            outbox.DuplicateDetectionWindow = options.DuplicateDetectionWindow;
            if (options.DeliveryPollInterval is { } interval)
            {
                outbox.QueryDelay = interval;
            }

            // The lock statements follow the context's provider (§4.1 row 6): the engine's own two
            // providers with their defaults, which is what UsePostgres() / UseSqlServer() install.
            outbox.LockStatementProvider = new ProviderLockStatements(new PostgresLockStatementProvider(), new SqlServerLockStatementProvider());
        }, options.Inbox);

    /// <summary>Fails the host's start, in words, when the outbox's context runs on a provider with no lock statements.</summary>
    internal sealed class OutboxProviderCheck<TContext>(IServiceScopeFactory scopes) : IHostedService
        where TContext : DbContext
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TContext>();
            _ = new ProviderLockStatements(new PostgresLockStatementProvider(), new SqlServerLockStatementProvider())
                .For(context.Database.ProviderName, typeof(TContext).Name);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Delegates to the PostgreSQL or SQL Server lock statements by the context's EF provider.</summary>
    internal sealed class ProviderLockStatements(ILockStatementProvider postgres, ILockStatementProvider sqlServer) : ILockStatementProvider
    {
        public string GetRowLockStatement<T>(DbContext context)
            where T : class
            => For(context).GetRowLockStatement<T>(context);

        public string GetRowLockStatement<T>(DbContext context, params string[] propertyNames)
            where T : class
            => For(context).GetRowLockStatement<T>(context, propertyNames);

        public string GetOutboxStatement(DbContext context) => For(context).GetOutboxStatement(context);

        private ILockStatementProvider For(DbContext context) => For(context.Database.ProviderName, context.GetType().Name);

        /// <summary>The lock statements for an EF provider name; any other provider is refused in words.</summary>
        internal ILockStatementProvider For(string? providerName, string contextName)
        {
            var provider = providerName ?? "";
            if (provider.Contains("Npgsql", StringComparison.Ordinal))
            {
                return postgres;
            }

            if (provider.Contains("SqlServer", StringComparison.Ordinal))
            {
                return sqlServer;
            }

            throw new InvalidOperationException(
                $"The outbox on '{contextName}' runs on PostgreSQL or SQL Server; its EF provider is '{provider}'.");
        }
    }
}
