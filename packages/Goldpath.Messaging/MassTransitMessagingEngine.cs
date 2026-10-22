using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// The MassTransit 8.x engine behind <see cref="GoldpathMessagingBuilder"/>: it turns what the builder
/// recorded into the same registrations the MassTransit-typed overloads make, so both vocabularies
/// produce one bus — the same queues, the same pipeline, the same tables. Internal until the adapter
/// package is extracted (RFC goldpath-messaging-engines, Part 4/4).
/// </summary>
internal static class MassTransitMessagingEngine
{
    private static readonly System.Reflection.MethodInfo s_addHandler =
        typeof(GoldpathHandlerRegistrationExtensions).GetMethod(nameof(GoldpathHandlerRegistrationExtensions.AddGoldpathHandler))!;

    private static readonly System.Reflection.MethodInfo s_composeOutbox =
        typeof(MassTransitMessagingEngine).GetMethod(nameof(ComposeOutbox), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

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
            s_addHandler.MakeGenericMethod(eventType, handlerType).Invoke(null, [bus]);
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
                    if (configuration.GetConnectionString(messaging.RabbitMqConnectionName) is { Length: > 0 } connection)
                    {
                        cfg.Host(new Uri(connection));
                    }

                    cfg.ConfigureGoldpathEndpoints(registration);
                });
                break;
            case GoldpathMessagingTransport.InMemory:
                bus.UsingInMemory((registration, cfg) => cfg.ConfigureGoldpathEndpoints(registration));
                break;
            default:
                throw new InvalidOperationException($"No transport composition for {messaging.Transport}.");
        }
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
