using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// Composition entry points — the ladder's fourth rider (campaign RFC D2/D4). Declare
/// types with <c>AddGoldpathCampaign</c>, map the store with <c>modelBuilder.AddGoldpathCampaign()</c>,
/// schedule the pacer through Jobs with <c>jobs.AddGoldpathCampaignJobs&lt;TContext&gt;()</c>, and
/// register the handlers inside YOUR AddGoldpathMessaging block with
/// <c>messaging.AddGoldpathCampaign&lt;TContext&gt;()</c> — campaign REQUIRES a broker (D8).
/// </summary>
public static class GoldpathCampaignExtensions
{
    /// <summary>Registers the engine, the campaign types and the pacer job.</summary>
    public static TBuilder AddGoldpathCampaign<TBuilder, TContext>(this TBuilder builder, Action<GoldpathCampaignOptions> configure)
        where TBuilder : IHostApplicationBuilder
        where TContext : DbContext
    {
        var options = new GoldpathCampaignOptions();
        configure(options);
        builder.Services.AddSingleton(options);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<GoldpathCampaignEngine<TContext>>();
        builder.Services.TryAddSingleton<GoldpathCampaignAdminService<TContext>>();
        builder.Services.TryAddScoped<GoldpathCampaignPacerJob<TContext>>();
        return builder;
    }

    /// <summary>
    /// Schedules the pacer on the Jobs module. The ~1-minute cron only guarantees a
    /// leader EXISTS — each fire leads in-memory for one <see cref="GoldpathCampaignOptions.LeadershipSlice"/>
    /// (campaign RFC D2); pacing never rides the cron granularity.
    /// </summary>
    public static GoldpathJobsOptions AddGoldpathCampaignJobs<TContext>(
        this GoldpathJobsOptions jobs,
        string pacerCron = "0 * * * * ?",
        TimeSpan? deadline = null)
        where TContext : DbContext
    {
        jobs.AddJob<GoldpathCampaignPacerJob<TContext>>(j =>
        {
            j.Cron = pacerCron;
            j.Deadline = deadline ?? TimeSpan.FromMinutes(5);
            // MaxParallelChunks stays 1: the plan is a single "lead" chunk by design —
            // one leader per fleet is the concurrency model (constraint 1).
        });
        return jobs;
    }

    /// <summary>
    /// Registers the item handler (competing, claim-before-execute) and the batching outcome
    /// handler on YOUR message path — call this inside the AddGoldpathMessaging configure block.
    /// Both drain the queue the fleet already has, named after <typeparamref name="TContext"/>,
    /// and the sink takes outcomes 200 at a time or every 500 ms, one batch in flight.
    /// Call it once per message path.
    /// </summary>
    /// <exception cref="InvalidOperationException">Campaign is already registered on this message path.</exception>
    public static GoldpathMessagingBuilder AddGoldpathCampaign<TContext>(this GoldpathMessagingBuilder messaging)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var queue = GoldpathCampaignEndpoints.QueueOf<TContext>();
        messaging.AddHandler<GoldpathCampaignItemMessage, GoldpathCampaignItemHandler<TContext>>(queue);
        messaging.AddBatchHandler<GoldpathCampaignOutcomeMessage, GoldpathCampaignOutcomeHandler<TContext>>(GoldpathCampaignEndpoints.OutcomeBatch, queue);
        return messaging;
    }

    /// <summary>
    /// The MassTransit-typed registration, kept so an app still on the <c>AddGoldpathMessaging(bus =&gt; …)</c>
    /// block (the templates, until the preview.11 release moves them) composes campaign unchanged: it registers
    /// the same two handlers on the same queue as <see cref="AddGoldpathCampaign{TContext}(GoldpathMessagingBuilder)"/>.
    /// It leaves with the other MassTransit-typed overloads in G1 Part 4/4 (RFC goldpath-messaging-engines §4.1 row 13).
    /// </summary>
    public static IBusRegistrationConfigurator AddGoldpathCampaignConsumers<TContext>(this IBusRegistrationConfigurator configurator)
        where TContext : DbContext
    {
        var queue = GoldpathCampaignEndpoints.QueueOf<TContext>();
        configurator.TryAddScoped<GoldpathCampaignItemHandler<TContext>>();
        configurator.AddConsumer<GoldpathIntegrationEventConsumer<GoldpathCampaignItemMessage, GoldpathCampaignItemHandler<TContext>>>()
            .Endpoint(endpoint => endpoint.Name = queue);
        configurator.TryAddScoped<GoldpathCampaignOutcomeHandler<TContext>>();
        var grouping = GoldpathCampaignEndpoints.OutcomeBatch;
        configurator.AddConsumer<GoldpathCampaignOutcomeBatchConsumer<TContext>>((_, consumer) => consumer.Options<BatchOptions>(batch => batch
                .SetMessageLimit(grouping.MessageLimit)
                .SetTimeLimit(grouping.TimeLimit)
                .SetConcurrencyLimit(grouping.ConcurrencyLimit)))
            .Endpoint(endpoint => endpoint.Name = queue);
        return configurator;
    }
}

/// <summary>
/// The outcome handler behind <see cref="GoldpathCampaignExtensions.AddGoldpathCampaignConsumers{TContext}"/>: the
/// MassTransit batch delivered to the seam's batch handler, as the builder's engine does. Leaves with that overload
/// (G1 Part 4/4).
/// </summary>
internal sealed class GoldpathCampaignOutcomeBatchConsumer<TContext>(GoldpathCampaignOutcomeHandler<TContext> handler)
    : IConsumer<Batch<GoldpathCampaignOutcomeMessage>>
    where TContext : DbContext
{
    public Task Consume(ConsumeContext<Batch<GoldpathCampaignOutcomeMessage>> context)
    {
        var attempt = context.GetRetryAttempt();
        var batch = context.Message
            .Select(delivery => new IntegrationEvent<GoldpathCampaignOutcomeMessage>(
                delivery.Message,
                new IntegrationEventContext(
                    delivery.MessageId,
                    delivery.Headers.Get<string>(GoldpathHeaders.CorrelationId),
                    delivery.Headers.Get<string>(GoldpathHeaders.TenantId) is { } raw && TenantId.TryCreate(raw, out var tenant) ? tenant : null,
                    attempt,
                    delivery.Headers.GetAll().ToDictionary(h => h.Key, h => (object?)h.Value, StringComparer.Ordinal))))
            .ToList();
        return handler.HandleAsync(batch, context.CancellationToken);
    }
}
