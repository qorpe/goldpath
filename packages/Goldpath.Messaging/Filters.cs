using System.Diagnostics;
using MassTransit;

namespace Goldpath;

/// <summary>
/// Message-scoped tenant holder set by the consume filter — the message-seam counterpart of
/// the HTTP tenant resolution. Ring B MultiTenancy composes with this; application code reads
/// <see cref="ITenantContext"/>.
/// </summary>
public sealed class GoldpathMessageTenantContext : ITenantContext
{
    /// <summary>The tenant restored from the message headers, or <see langword="null"/>.</summary>
    public TenantId? Current { get; set; }
}

/// <summary>
/// Publish-side guard and stamping (the event boundary, RFC goldpath-messaging D2): only
/// <see cref="IIntegrationEvent"/>-marked types may cross the service boundary (GP0401 at
/// runtime until the analyzer ships); tenant and correlation headers are stamped from the
/// ambient context.
/// </summary>
public sealed class GoldpathPublishFilter<T> : IFilter<PublishContext<T>>
    where T : class
{
    private readonly ITenantContext? _tenantContext;

    /// <summary>Creates the filter; the tenant context is optional (single-tenant deployments).</summary>
    public GoldpathPublishFilter(IServiceProvider serviceProvider)
        => _tenantContext = (ITenantContext?)serviceProvider.GetService(typeof(ITenantContext));

    /// <inheritdoc />
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        var messageType = typeof(T);
        if (context.Message is not IIntegrationEvent
            && messageType.Namespace?.StartsWith("MassTransit", StringComparison.Ordinal) is not true)
        {
            throw new InvalidOperationException(
                $"'{messageType.Name}' is published to the bus but does not implement IIntegrationEvent. "
                + "Broker-bound events must carry the marker; in-process domain events are Mediant "
                + "notifications and never touch the bus (Goldpath rule GP0401).");
        }

        if (_tenantContext?.Current is { } tenant)
        {
            context.Headers.Set(GoldpathHeaders.TenantId, tenant.Value);
        }

        if (CorrelationOf(Activity.Current) is { } correlationId)
        {
            context.Headers.Set(GoldpathHeaders.CorrelationId, correlationId);
        }

        return next.Send(context);
    }

    /// <inheritdoc />
    public void Probe(ProbeContext context) => context.CreateFilterScope("goldpathPublish");

    // The tag sits on the REQUEST (or consume) activity; by the time this filter runs,
    // MassTransit has started its own child activity for the send, so Activity.Current
    // is one level down. Walk up — found by the Messaging mutation gate (2026-09-03):
    // the old single-level read never stamped the header from inside a real request.
    private static string? CorrelationOf(Activity? activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
        {
            if (current.GetTagItem("goldpath.correlation_id") is string correlationId)
            {
                return correlationId;
            }
        }

        return null;
    }
}

/// <summary>
/// Consume-side restoration: the tenant header repopulates the message-scoped
/// <see cref="GoldpathMessageTenantContext"/>, and the tenant and the correlation id are tagged on the
/// current Activity (<c>goldpath.tenant_id</c>, <c>goldpath.correlation_id</c>) so logs/traces line up
/// across the hop.
/// </summary>
public sealed class GoldpathConsumeFilter<T> : IFilter<ConsumeContext<T>>
    where T : class
{
    private readonly GoldpathMessageTenantContext _tenantContext;

    /// <summary>Creates the filter over the message-scoped tenant holder.</summary>
    public GoldpathConsumeFilter(GoldpathMessageTenantContext tenantContext) => _tenantContext = tenantContext;

    /// <inheritdoc />
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var previousAmbient = GoldpathAmbientTenant.Current;
        if (context.Headers.TryGetHeader(GoldpathHeaders.TenantId, out var raw)
            && raw is string value
            && TenantId.TryCreate(value, out var tenant))
        {
            _tenantContext.Current = tenant;
            GoldpathAmbientTenant.Current = tenant;   // ambient truth: EF filters/guards see the origin tenant
            Activity.Current?.SetTag("goldpath.tenant_id", tenant.Value);
        }

        if (context.Headers.TryGetHeader(GoldpathHeaders.CorrelationId, out var correlation)
            && correlation is string correlationId)
        {
            Activity.Current?.SetTag("goldpath.correlation_id", correlationId);
        }

        try
        {
            await next.Send(context);
        }
        finally
        {
            GoldpathAmbientTenant.Current = previousAmbient;
        }
    }

    /// <inheritdoc />
    public void Probe(ProbeContext context) => context.CreateFilterScope("goldpathConsume");
}
