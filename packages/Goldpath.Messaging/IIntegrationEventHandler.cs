using System.Reflection;
using System.Text;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Goldpath;

/// <summary>
/// The consume seam (ADR-0013, messaging-exit RFC §5): how an application handles an
/// integration event, expressed in Goldpath's own vocabulary instead of the transport
/// library's. The publish seam (<see cref="IIntegrationEventPublisher"/>) shipped first;
/// this is its other half — with both, an adopter's code names no bus type at all, and
/// the engine behind the seam can change without editing a single handler.
/// </summary>
/// <typeparam name="TEvent">The event — must carry the <see cref="IIntegrationEvent"/> marker.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : class, IIntegrationEvent
{
    /// <summary>
    /// Handles one delivery. Runs inside the consume pipeline the floor composes: the
    /// tenant restored from the message, retry/redelivery per <see cref="GoldpathMessagingOptions"/>,
    /// and the inbox when the outbox is composed — a throw here is a retry, then the error queue.
    /// </summary>
    Task HandleAsync(TEvent integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// What the pipeline knows about a delivery, without the library's context type: the
/// message identity (the inbox's dedup key), the correlation the publish stamped, the
/// tenant it carried, which attempt this is, and the raw headers for anything else.
/// </summary>
/// <param name="MessageId">The transport's message id — what the inbox dedups on.</param>
/// <param name="CorrelationId">The correlation the publisher stamped (<see cref="GoldpathHeaders.CorrelationId"/>), or null.</param>
/// <param name="Tenant">The tenant restored from the message, or null on a single-tenant estate.</param>
/// <param name="RetryAttempt">0 on the first delivery; the immediate-retry count after.</param>
/// <param name="Headers">Every header on the message, as delivered.</param>
public sealed record IntegrationEventContext(
    Guid? MessageId,
    string? CorrelationId,
    TenantId? Tenant,
    int RetryAttempt,
    IReadOnlyDictionary<string, object?> Headers);

/// <summary>
/// The adapter behind the seam: ONE library consumer per (event, handler) pair, resolving
/// the adopter's handler from the message scope. Public because the library constructs it
/// through DI; an adopter never names it — <c>bus.AddGoldpathHandler&lt;TEvent, THandler&gt;()</c>
/// does.
/// </summary>
public sealed class GoldpathIntegrationEventConsumer<TEvent, THandler> : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
    where THandler : class, IIntegrationEventHandler<TEvent>
{
    private readonly THandler _handler;
    private readonly GoldpathMessageTenantContext _tenant;

    /// <summary>Resolved per message scope, after the consume filter restored the tenant.</summary>
    public GoldpathIntegrationEventConsumer(THandler handler, GoldpathMessageTenantContext tenant)
    {
        _handler = handler;
        _tenant = tenant;
    }

    /// <inheritdoc />
    public Task Consume(ConsumeContext<TEvent> context)
    {
        var eventContext = new IntegrationEventContext(
            context.MessageId,
            context.Headers.Get<string>(GoldpathHeaders.CorrelationId),
            _tenant.Current,
            context.GetRetryAttempt(),
            IntegrationEventHeaders.Of(context));
        return _handler.HandleAsync(context.Message, eventContext, context.CancellationToken);
    }
}

/// <summary>Every header on a delivery, as delivered — what <see cref="IntegrationEventContext.Headers"/> carries.</summary>
internal static class IntegrationEventHeaders
{
    public static IReadOnlyDictionary<string, object?> Of(ConsumeContext context)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var header in context.Headers.GetAll())
        {
            headers[header.Key] = header.Value;
        }

        return headers;
    }
}

/// <summary>
/// The queue a handler drains, derived from its NAME the way the floor always did for
/// consumers: <c>WorkItemQueuedHandler</c> and <c>WorkItemQueuedConsumer</c> both drain
/// <c>work-item-queued</c> — so moving a consumer to the seam changes no queue (the exit
/// RFC's S4: a mixed fleet keeps its wire).
/// </summary>
public static class GoldpathHandlerEndpoints
{
    /// <summary>The kebab-case endpoint name for <paramref name="handlerType"/>.</summary>
    public static string NameOf(Type handlerType)
    {
        var name = handlerType.Name;
        var generic = name.IndexOf('`');
        if (generic >= 0)
        {
            name = name[..generic];
        }

        foreach (var suffix in new[] { "Handler", "Consumer" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}

/// <summary>Registration of handlers on the bus — the seam's registration vocabulary.</summary>
public static class GoldpathHandlerRegistrationExtensions
{
    /// <summary>
    /// Registers <typeparamref name="THandler"/> for <typeparamref name="TEvent"/>: the handler
    /// joins the message scope, and its queue is named after it exactly as a consumer's
    /// would be (<see cref="GoldpathHandlerEndpoints.NameOf"/>).
    /// </summary>
    public static IBusRegistrationConfigurator AddGoldpathHandler<TEvent, THandler>(this IBusRegistrationConfigurator configurator)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        AddHandlerAt<TEvent, THandler>(configurator, GoldpathHandlerEndpoints.NameOf(typeof(THandler)));
        return configurator;
    }

    /// <summary>Registers the handler on the endpoint named <paramref name="endpointName"/>; endpoints that share a name share a queue.</summary>
    internal static void AddHandlerAt<TEvent, THandler>(IBusRegistrationConfigurator configurator, string endpointName)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        configurator.TryAddScoped<THandler>();
        configurator.AddConsumer<GoldpathIntegrationEventConsumer<TEvent, THandler>>()
            .Endpoint(endpoint => endpoint.Name = endpointName);
    }

    /// <summary>
    /// Registers every <see cref="IIntegrationEventHandler{TEvent}"/> implementation found in
    /// <paramref name="assemblies"/> (a handler implementing several event interfaces joins
    /// once per event). An open generic type is skipped: it cannot be an endpoint, and a closed type
    /// deriving from it is found.
    /// </summary>
    public static IBusRegistrationConfigurator AddGoldpathHandlers(this IBusRegistrationConfigurator configurator, params Assembly[] assemblies)
    {
        var open = typeof(IIntegrationEventHandler<>);
        var register = typeof(GoldpathHandlerRegistrationExtensions).GetMethod(nameof(AddGoldpathHandler))!;
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }))
            {
                foreach (var contract in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == open))
                {
                    register.MakeGenericMethod(contract.GetGenericArguments()[0], type).Invoke(null, [configurator]);
                }
            }
        }

        return configurator;
    }
}
