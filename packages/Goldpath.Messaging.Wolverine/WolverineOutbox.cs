using System.Diagnostics;
using System.Text.Json;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>
/// The publish seam on this adapter (RFC goldpath-messaging-exit §5). With an outbox composed, a publish is an
/// <c>OutboxMessage</c> row — the 8.x envelope, in the 8.x table (RFC goldpath-messaging-engines §4.4, §9 Q4) —
/// added to the application's own context, so it commits with the caller's <c>SaveChanges</c> and the relay
/// delivers it; with none, it goes straight to the broker. Tenant and correlation are stamped from the ambient
/// context as the default engine stamps them.
/// </summary>
internal sealed class WolverineIntegrationEventPublisher(IServiceProvider services, GoldpathWire wire, WolverineEngineState state) : IIntegrationEventPublisher
{
    private OutboxState? _outbox;

    public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (services.GetService<ITenantContext>()?.Current is { } tenant)
        {
            headers[GoldpathHeaders.TenantId] = tenant.Value;
        }

        if (CorrelationOf(Activity.Current) is { } correlationId)
        {
            headers[GoldpathHeaders.CorrelationId] = correlationId;
        }

        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var envelope = wire.Envelope(typeof(TEvent), integrationEvent, messageId, headers, now);
        if (state.OutboxContext is not { } contextType)
        {
            await wire.PublishAsync(GoldpathWire.ExchangeOf(typeof(TEvent)), messageId, envelope, cancellationToken);
            return;
        }

        var context = (DbContext)services.GetRequiredService(contextType);
        // One outbox per unit of work: the rows a SaveChanges commits together are delivered together, in order.
        // Once that state has been saved, a later publish in the same scope opens the next one.
        if (_outbox is null || context.Entry(_outbox).State != EntityState.Added)
        {
            _outbox = new OutboxState { OutboxId = Guid.NewGuid(), LockId = Guid.NewGuid(), Created = now.UtcDateTime };
            context.Set<OutboxState>().Add(_outbox);
        }

        var outboxId = _outbox.OutboxId;

        context.Set<OutboxMessage>().Add(new OutboxMessage
        {
            OutboxId = outboxId,
            MessageId = messageId,
            ConversationId = messageId,
            ContentType = GoldpathWire.ContentType,
            MessageType = GoldpathWire.UrnOf(typeof(TEvent)),
            Body = System.Text.Encoding.UTF8.GetString(envelope),
            Headers = JsonSerializer.Serialize(headers),
            SentTime = now.UtcDateTime,
            DestinationAddress = wire.AddressOf(GoldpathWire.ExchangeOf(typeof(TEvent))),
        });
    }

    // The tag sits on the request (or consume) activity, possibly some levels up: walk to it, as the default engine does.
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
/// Delivers committed outbox rows (RFC goldpath-messaging-engines §4.4): it locks one undelivered
/// <c>OutboxState</c> with <c>FOR UPDATE SKIP LOCKED</c>, so two processes never deliver one row, publishes
/// its messages in sequence with broker confirms, and removes them in the same transaction. A broker that
/// refuses rolls the transaction back and the rows wait for the next pass: the outbox is the buffer.
/// </summary>
internal sealed class WolverineOutboxRelay<TContext>(
    IServiceScopeFactory scopes, GoldpathWire wire, GoldpathOutboxOptions options, ILogger<WolverineOutboxRelay<TContext>> logger) : BackgroundService
    where TContext : DbContext
{
    private static readonly TimeSpan s_defaultPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_confirmTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_inboxSweep = TimeSpan.FromMinutes(1);
    private DateTime _nextSweep = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = options.DeliveryPollInterval ?? s_defaultPoll;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await DeliverOneAsync(stoppingToken))
                {
                }

                if (DateTime.UtcNow >= _nextSweep)
                {
                    await SweepInboxAsync(stoppingToken);
                    _nextSweep = DateTime.UtcNow + s_inboxSweep;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The outbox relay could not deliver; the rows stay and are taken again in {Poll}.", poll);
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

    /// <summary>Delivers one outbox's messages; false when none is waiting.</summary>
    internal async Task<bool> DeliverOneAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var states = context.Model.FindEntityType(typeof(OutboxState))
            ?? throw new InvalidOperationException($"'{typeof(TContext).Name}' maps no outbox tables; call modelBuilder.AddGoldpathOutboxEntities().");
        var table = Quote(states.GetSchema(), states.GetTableName()!);
        var delivered = Quote(null, states.FindProperty(nameof(OutboxState.Delivered))!.GetColumnName());
        var created = Quote(null, states.FindProperty(nameof(OutboxState.Created))!.GetColumnName());

        // Identifiers only, read from the EF model; nothing a caller supplies reaches this text.
        var oldestUnlocked = $"SELECT * FROM {table} WHERE {delivered} IS NULL ORDER BY {created} LIMIT 1 FOR UPDATE SKIP LOCKED";

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var state = await context.Set<OutboxState>()
            .FromSqlRaw(oldestUnlocked)
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return false;
        }

        var messages = await context.Set<OutboxMessage>()
            .Where(m => m.OutboxId == state.OutboxId)
            .OrderBy(m => m.SequenceNumber)
            .ToListAsync(cancellationToken);
        foreach (var message in messages)
        {
            // A broker that accepts the connection but never confirms (paused, blocked on a resource alarm) must not
            // hold the locked row for ever: the pass gives up, rolls back, and the row is taken again.
            using var confirm = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            confirm.CancelAfter(s_confirmTimeout);
            await wire.PublishAsync(GoldpathWire.ExchangeOfMessageTypes(message.MessageType), message.MessageId, System.Text.Encoding.UTF8.GetBytes(message.Body), confirm.Token);
        }

        context.Set<OutboxMessage>().RemoveRange(messages);
        context.Set<OutboxState>().Remove(state);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Forgets inbox rows delivered longer ago than the duplicate-detection window, by the default engine's own
    /// rule (<c>Delivered</c> before now less the window): the inbox is a window, not a history, and without this the
    /// table only grows. The rule reads a column both engines write, so in a mixed fleet the rows either one wrote
    /// are forgotten by whichever runs the sweep; the default engine leaves <c>ExpirationTime</c> empty, so a rule
    /// on that column would keep its rows for ever.
    /// </summary>
    internal async Task<int> SweepInboxAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var cutoff = DateTime.UtcNow - options.DuplicateDetectionWindow;
        return await context.Set<InboxState>()
            .Where(i => i.Delivered != null && i.Delivered < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static string Quote(string? schema, string name)
        => schema is null ? $"\"{name}\"" : $"\"{schema}\".\"{name}\"";
}

/// <summary>Refuses, at start, an outbox on a context this adapter's relay cannot lock rows on.</summary>
internal sealed class WolverineOutboxProviderCheck<TContext>(IServiceScopeFactory scopes) : IHostedService
    where TContext : DbContext
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<TContext>().Database.ProviderName ?? "";
        if (!provider.Contains("Npgsql", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Wolverine adapter's outbox on '{typeof(TContext).Name}' runs on PostgreSQL; its EF provider is '{provider}'. " +
                "SQL Server is not built yet (open-threads T18).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
