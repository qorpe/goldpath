using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// The message-path floor: MassTransit (8.x OSS line — RFC D1) composed with corporate
/// conventions. The transport is wired by the template from the manifest
/// (<c>providers.broker</c>) inside <c>configureBus</c>; this package stays transport-neutral.
/// </summary>
public static class GoldpathMessagingExtensions
{
    /// <summary>
    /// Registers MassTransit with the Goldpath conventions: kebab-case endpoint naming, the
    /// message-scoped tenant holder, and options bound from <c>Goldpath:Messaging</c>.
    /// Configure consumers AND the transport in <paramref name="configureBus"/>; inside the
    /// transport callback, call <see cref="ConfigureGoldpathEndpoints"/> to apply the corporate
    /// pipeline (guard/propagation filters + retry) and endpoint conventions.
    /// </summary>
    public static TBuilder AddGoldpathMessaging<TBuilder>(
        this TBuilder builder,
        Action<IBusRegistrationConfigurator> configureBus,
        Action<GoldpathMessagingOptions>? configureOptions = null)
        where TBuilder : IHostApplicationBuilder
    {
        var options = new GoldpathMessagingOptions();
        builder.Configuration.GetSection("Goldpath:Messaging").Bind(options);
        configureOptions?.Invoke(options);
        return builder.AddGoldpathMessagingCore(configureBus, options);
    }

    /// <summary>What both registrations share: the options as given, the tenant holder, the publish seam and the bus.</summary>
    private static TBuilder AddGoldpathMessagingCore<TBuilder>(this TBuilder builder, Action<IBusRegistrationConfigurator> configureBus, GoldpathMessagingOptions options)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddSingleton(options);

        builder.Services.TryAddScoped<GoldpathMessageTenantContext>();
        builder.Services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<GoldpathMessageTenantContext>());

        // The publish seam (RFC goldpath-messaging-exit §5): an application publishes through
        // Goldpath's own contract, so a transport change never reaches an adopter's command
        // handlers. Scoped, because the outbox's publish endpoint is.
        builder.Services.TryAddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>();

        builder.Services.AddMassTransit(bus =>
        {
            bus.SetKebabCaseEndpointNameFormatter();
            configureBus(bus);
        });

        return builder;
    }

    /// <summary>
    /// Registers the message path in Goldpath's own vocabulary (RFC goldpath-messaging-engines §4.1 row 5):
    /// handlers, the outbox and the transport are declared on a <see cref="GoldpathMessagingBuilder"/>, and
    /// the engine composes them with the corporate pipeline on every endpoint it creates (guard and
    /// propagation filters, retry, redelivery, endpoint conventions), so no transport callback has to
    /// remember to apply it. Options bind from <c>Goldpath:Messaging</c> first; <see cref="GoldpathMessagingBuilder.Options"/>
    /// changes made in <paramref name="configure"/> win.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transport was selected (call <see cref="GoldpathMessagingBuilder.UseRabbitMq"/>).</exception>
    public static TBuilder AddGoldpathMessaging<TBuilder>(this TBuilder builder, Action<GoldpathMessagingBuilder> configure)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(configure);
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());
        builder.Configuration.GetSection("Goldpath:Messaging").Bind(messaging.Options);
        configure(messaging);
        if (messaging.Transport == GoldpathMessagingTransport.None)
        {
            throw new InvalidOperationException(
                "AddGoldpathMessaging: the message path has no transport. Call messaging.UseRabbitMq() (the manifest's providers.broker).");
        }

        MassTransitMessagingEngine.Validate(messaging, builder.Services);
        return builder.AddGoldpathMessagingCore(bus => MassTransitMessagingEngine.Compose(messaging, bus, builder.Configuration), messaging.Options);
    }

    /// <summary>
    /// Applies the Goldpath message pipeline to a transport: the integration-event guard and
    /// tenant/correlation propagation filters, immediate retry, delayed redelivery (when
    /// intervals are configured), and MassTransit endpoint conventions. Call this inside the
    /// transport callback (e.g. <c>UsingRabbitMq((ctx, cfg) => { …; cfg.ConfigureGoldpathEndpoints(ctx); })</c>).
    /// </summary>
    public static void ConfigureGoldpathEndpoints<TEndpoint>(this IBusFactoryConfigurator<TEndpoint> configurator, IBusRegistrationContext context)
        where TEndpoint : IReceiveEndpointConfigurator
    {
        var options = context.GetRequiredService<GoldpathMessagingOptions>();

        configurator.UsePublishFilter(typeof(GoldpathPublishFilter<>), context);
        configurator.UseConsumeFilter(typeof(GoldpathConsumeFilter<>), context);

        if (options.Retry.RedeliveryIntervals.Count > 0)
        {
            configurator.UseDelayedRedelivery(retry => retry.Intervals([.. options.Retry.RedeliveryIntervals]));
        }

        configurator.UseMessageRetry(retry => retry.Immediate(options.Retry.ImmediateCount));
        configurator.ConfigureEndpoints(context);
    }

    /// <summary>
    /// Composes MassTransit's Entity Framework transactional outbox with the service's
    /// DbContext (RFC D3: publish commits atomically with business data; consumer inbox dedup
    /// included). The DB lock provider (UsePostgres/UseSqlServer) is set by the template in
    /// <paramref name="configure"/> — provider-neutral here.
    /// </summary>
    public static void AddGoldpathOutbox<TContext>(
        this IBusRegistrationConfigurator configurator,
        Action<IEntityFrameworkOutboxConfigurator>? configure = null)
        where TContext : DbContext
        => ComposeOutbox<TContext>(configurator, configure, inbox: true);

    /// <summary>The outbox composition both registrations share; <paramref name="inbox"/> adds the consumer-side dedup.</summary>
    internal static void ComposeOutbox<TContext>(
        IBusRegistrationConfigurator configurator,
        Action<IEntityFrameworkOutboxConfigurator>? configure,
        bool inbox)
        where TContext : DbContext
    {
        configurator.AddEntityFrameworkOutbox<TContext>(outbox =>
        {
            outbox.DuplicateDetectionWindow = TimeSpan.FromMinutes(30);
            outbox.UseBusOutbox();
            configure?.Invoke(outbox);
        });

        if (inbox)
        {
            // Consumer-side inbox: every receive endpoint gets exactly-once-processing dedup
            // automatically — without this callback only the publish side would be outboxed.
            configurator.AddConfigureEndpointsCallback((context, _, endpointConfigurator) =>
                endpointConfigurator.UseEntityFrameworkOutbox<TContext>(context));
        }
    }
}
