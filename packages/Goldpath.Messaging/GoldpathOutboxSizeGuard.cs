using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json.Nodes;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The message-size floor (#458), on every engine: a body above <see cref="GoldpathMessagingOptions.MaxMessageBytes"/>
/// is one the broker refuses on every attempt, and an outbox row the broker refuses is retried for ever while every
/// row committed after it waits. It lives in the floor, not in an adapter, because both adapters write the same
/// <see cref="OutboxMessage"/> rows into the application's own context, so one rule reads both and no engine can
/// answer differently.
/// </summary>
internal static class GoldpathOutboxSize
{
    /// <summary>The marker header a failed row carries, with the reason; its presence is how the runbook finds one.</summary>
    internal const string FailedHeader = "Goldpath-Outbox-Failed";

    /// <summary>The outbox the row was detached from, kept so an operator can put it back (ops/messaging.md).</summary>
    internal const string FailedOutboxIdHeader = "Goldpath-Outbox-Failed-OutboxId";

    /// <summary>The inbox row (message id, consumer id) a row published from a consumer was detached from.</summary>
    internal const string FailedInboxHeader = "Goldpath-Outbox-Failed-Inbox";

    private static readonly Meter s_meter = new("Goldpath.Messaging");

    /// <summary>
    /// Bodies above the limit, by where they were stopped: <c>stage=publish</c> (refused at the seam, nothing committed)
    /// or <c>stage=outbox</c> (a committed row marked failed in place).
    /// </summary>
    internal static readonly Counter<long> Oversized = s_meter.CreateCounter<long>(
        "goldpath.messaging.outbox.oversized", unit: "{message}",
        description: "Integration events above Goldpath:Messaging:MaxMessageBytes: refused at publish, or a committed outbox row marked failed in place.");

    /// <summary>The bytes the broker will be handed for <paramref name="body"/>: its UTF-8 length.</summary>
    internal static int BytesOf(string body) => Encoding.UTF8.GetByteCount(body);

    /// <summary>
    /// The longest body, in UTF-16 characters, that cannot be above <paramref name="maxBytes"/> UTF-8 bytes: a character
    /// is at most three bytes (a surrogate pair is two characters and four bytes), so only a body longer than this can be.
    /// </summary>
    internal static int CandidateLength(int maxBytes) => maxBytes / 3;

    /// <summary>Refuses a <see cref="GoldpathMessagingOptions.MaxMessageBytes"/> that is not positive, at composition.</summary>
    internal static void Validate(GoldpathMessagingOptions options)
    {
        if (options.MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(GoldpathMessagingOptions.MaxMessageBytes), options.MaxMessageBytes,
                "Goldpath:Messaging:MaxMessageBytes must be positive: the largest body, in bytes, the broker accepts (RabbitMQ 4's default is 16777216).");
        }
    }

    /// <summary>
    /// Wraps the engine's <see cref="IIntegrationEventPublisher"/> in <see cref="GoldpathSizeGuardPublisher"/> and, with
    /// an outbox composed, starts the sweeper that marks a committed oversized row failed in place.
    /// </summary>
    internal static void Compose(IServiceCollection services, GoldpathMessagingComposition composition)
    {
        if (composition.OutboxContext is not { } context)
        {
            // No outbox: the publish goes straight to the broker, whose refusal reaches the caller as the exception the
            // engine raises. There is no row to hold anything back, so there is nothing to sweep.
            return;
        }

        var engine = services.LastOrDefault(d => d.ServiceType == typeof(IIntegrationEventPublisher))
            ?? throw new InvalidOperationException("The messaging engine registered no IIntegrationEventPublisher.");
        services.Remove(engine);
        Func<IServiceProvider, IIntegrationEventPublisher> inner = engine switch
        {
            { ImplementationFactory: { } factory } => sp => (IIntegrationEventPublisher)factory(sp),
            { ImplementationInstance: IIntegrationEventPublisher instance } => _ => instance,
            { ImplementationType: { } type } => sp => (IIntegrationEventPublisher)ActivatorUtilities.CreateInstance(sp, type),
            _ => throw new InvalidOperationException("The messaging engine's IIntegrationEventPublisher registration has no implementation."),
        };
        services.Add(new ServiceDescriptor(typeof(IIntegrationEventPublisher), sp => new GoldpathSizeGuardPublisher(
            inner(sp), (DbContext)sp.GetRequiredService(context), sp.GetRequiredService<GoldpathMessagingOptions>()), engine.Lifetime));
        var sweeper = typeof(GoldpathOutboxOversizeSweeper<>).MakeGenericType(context);
        var outbox = composition.Outbox ?? new GoldpathOutboxOptions();
        // FIRST among the hosted services, which start in order: its first pass runs before the engine's bus starts,
        // so a row already oversized when the process starts is marked before a delivery pass can take it (a pass that
        // takes it closes the broker channel and waits out its send timeout before the rows behind it move).
        services.Insert(0, ServiceDescriptor.Singleton(typeof(IHostedService), sp => ActivatorUtilities.CreateInstance(sp, sweeper, outbox)));
    }
}

/// <summary>
/// The publish seam's size check (#458): after the engine has added the event's outbox row to the caller's context, a
/// row above the limit is taken out of the context again and the publish is refused, so the failure lands on the line
/// that published, before anything commits. It reads the row the engine wrote, not a second serialization, so the size
/// it judges is exactly the size the broker would be handed.
/// </summary>
internal sealed class GoldpathSizeGuardPublisher(IIntegrationEventPublisher inner, DbContext context, GoldpathMessagingOptions options) : IIntegrationEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        await inner.PublishAsync(integrationEvent, cancellationToken);
        var tracker = context.ChangeTracker;
        var detect = tracker.AutoDetectChangesEnabled;
        tracker.AutoDetectChangesEnabled = false;   // an added row is tracked as Added already; a full DetectChanges per publish is not needed
        try
        {
            foreach (var entry in tracker.Entries<OutboxMessage>().Where(e => e.State == EntityState.Added).ToList())
            {
                var body = entry.Entity.Body;
                if (body is null || body.Length <= GoldpathOutboxSize.CandidateLength(options.MaxMessageBytes))
                {
                    continue;
                }

                var bytes = GoldpathOutboxSize.BytesOf(body);
                if (bytes <= options.MaxMessageBytes)
                {
                    continue;
                }

                // Out of the unit of work: a caller that catches this and saves anyway must not commit the row.
                entry.State = EntityState.Detached;
                GoldpathOutboxSize.Oversized.Add(1, new KeyValuePair<string, object?>("stage", "publish"));
                throw new InvalidOperationException(
                    $"The integration event {typeof(TEvent).Name} is {bytes} bytes, above Goldpath:Messaging:MaxMessageBytes ({options.MaxMessageBytes}), " +
                    "the largest body the broker accepts. It was not published, and its outbox row is not part of this unit of work. " +
                    "Publish less: a reference to the data rather than the data, or several smaller events.");
            }
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detect;
        }
    }
}

/// <summary>
/// Marks a committed outbox row above the limit failed IN PLACE (#458, owner decision 2026-10-27), on every engine. The
/// row stays in the table with its body; it is detached from its outbox (and from the inbox row a consumer published it
/// under), so neither engine's delivery loop ever takes it again, and its headers say why and where it came from. The
/// rows behind it are then delivered as if it had never been written. The outbox schema is unchanged.
/// </summary>
internal sealed class GoldpathOutboxOversizeSweeper<TContext>(
    IServiceScopeFactory scopes, GoldpathMessagingOptions options, GoldpathOutboxOptions outbox, ILogger<GoldpathOutboxOversizeSweeper<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    // Matches the delivery loops' own default cadence: a row behind an oversized one waits at most about one pass.
    private static readonly TimeSpan s_defaultPoll = TimeSpan.FromSeconds(1);

    // The rows one query reads; a pass walks every candidate page by page, so a row long enough to be a candidate but
    // under the limit can never hide an oversized one behind it.
    internal const int PageSize = 10;

    // How many of the oldest undelivered outboxes a pass reads. Both engines' delivery loops take the OLDEST undelivered
    // outbox first, so a row holds the others back only from there; reading only the head keeps a pass from measuring
    // every body of a backlog (a broker outage) once a second. A row further back is found when it reaches the head.
    internal const int HeadOutboxes = 10;

    /// <summary>Runs one pass before the host goes on to start the bus; a pass that cannot run is logged, never fatal.</summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SweepAsync(cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "The outbox size sweep could not run at start; it runs again once the host has started.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = outbox.DeliveryPollInterval ?? s_defaultPoll;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A row locked by a delivery pass, a database that is down: the row stays and is taken again next pass.
                logger.LogWarning(e, "The outbox size sweep could not run; it runs again in {Poll}.", poll);
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Marks every oversized row attached to one of the oldest undelivered outboxes, or to an inbox, failed in place; returns how many it marked.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var max = options.MaxMessageBytes;
        var candidateLength = GoldpathOutboxSize.CandidateLength(max);

        // Only a row still attached to an outbox at the head, or to an inbox, can be the one a delivery pass is stuck on;
        // a row already marked is detached from both and is never read again. The length is in characters, a cheap bound
        // the database evaluates; the exact UTF-8 size is measured here, on the few rows long enough to be above it.
        var head = await context.Set<OutboxState>()
            .Where(s => s.Delivered == null)
            .OrderBy(s => s.Created)
            .Select(s => s.OutboxId)
            .Take(HeadOutboxes)
            .ToListAsync(cancellationToken);

        var marked = 0;
        var after = long.MinValue;
        while (true)
        {
            // A row a consumer published rides its inbox row instead (InboxMessageId), and those are few: each is
            // delivered when its consume completes.
            var page = await context.Set<OutboxMessage>()
                .Where(m => m.SequenceNumber > after
                    && ((m.OutboxId != null && head.Contains(m.OutboxId.Value)) || m.InboxMessageId != null)
                    && m.Body.Length > candidateLength)
                .OrderBy(m => m.SequenceNumber)
                .Select(m => m.SequenceNumber)
                .Take(PageSize)
                .ToListAsync(cancellationToken);
            foreach (var sequenceNumber in page)
            {
                marked += await MarkIfOversizedAsync(context, sequenceNumber, max, cancellationToken) ? 1 : 0;
            }

            if (page.Count < PageSize)
            {
                return marked;
            }

            after = page[^1];
        }
    }

    private async Task<bool> MarkIfOversizedAsync(TContext context, long sequenceNumber, int max, CancellationToken cancellationToken)
    {
        var row = await context.Set<OutboxMessage>().AsTracking().SingleOrDefaultAsync(m => m.SequenceNumber == sequenceNumber, cancellationToken);
        if (row is null || (row.OutboxId is null && row.InboxMessageId is null))
        {
            return false;   // delivered or marked since the query
        }

        var bytes = GoldpathOutboxSize.BytesOf(row.Body);
        if (bytes <= max)
        {
            context.Entry(row).State = EntityState.Detached;   // a long row under the limit: not ours to touch
            return false;
        }

        var headers = (string.IsNullOrEmpty(row.Headers) ? null : JsonNode.Parse(row.Headers) as JsonObject) ?? [];
        headers[GoldpathOutboxSize.FailedHeader] =
            $"{bytes} bytes exceeds MaxMessageBytes {max}; marked failed in place at {DateTimeOffset.UtcNow:O} and never sent";
        if (row.OutboxId is { } outboxId)
        {
            headers[GoldpathOutboxSize.FailedOutboxIdHeader] = outboxId.ToString();
        }

        if (row.InboxMessageId is { } inboxMessageId)
        {
            headers[GoldpathOutboxSize.FailedInboxHeader] = $"{inboxMessageId}/{row.InboxConsumerId}";
        }

        row.Headers = headers.ToJsonString();
        row.OutboxId = null;
        row.InboxMessageId = null;
        row.InboxConsumerId = null;
        await context.SaveChangesAsync(cancellationToken);
        context.Entry(row).State = EntityState.Detached;   // a pass may walk many pages; it holds no 16 MiB bodies
        GoldpathOutboxSize.Oversized.Add(1, new KeyValuePair<string, object?>("stage", "outbox"));

        // The id, the type and the size; never the body (it can carry personal data, and it is 16 MiB of it).
        logger.LogError(
            "Outbox message {MessageId} ({MessageType}, sequence {SequenceNumber}) is {Bytes} bytes, above Goldpath:Messaging:MaxMessageBytes {MaxMessageBytes}. " +
            "It was marked failed in place and will not be sent; the rows behind it are delivered. Recover it by the messaging runbook (\"An outbox row marked failed\").",
            row.MessageId, row.MessageType, row.SequenceNumber, bytes, max);
        return true;
    }
}
