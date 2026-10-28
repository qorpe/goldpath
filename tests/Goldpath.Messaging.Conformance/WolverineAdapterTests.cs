using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The Wolverine adapter's own decisions, which the conformance class cannot reach because its harness always
/// composes the inbox and one handler per endpoint: a handler's publish committed without the inbox, every handler
/// of one endpoint run for each delivery, and the inbox swept past its window. The adapter has no mutation scope
/// yet (open-threads T51), so these facts are what notices them break.
/// </summary>
[Collection("conformance")]
public sealed class WolverineAdapterTests(ConformanceContainers containers)
{
    private const string Engine = "wolverine";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public sealed class Relay : IIntegrationEvent
    {
        public Guid Id { get; set; }
    }

    /// <summary>Publishes a Ping through the seam and saves nothing itself.</summary>
    public sealed class RelayHandler(IIntegrationEventPublisher publisher) : IIntegrationEventHandler<Relay>
    {
        public Task HandleAsync(Relay integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
            => publisher.PublishAsync(new Ping { Id = integrationEvent.Id, Payload = "relayed" }, cancellationToken);
    }

    public sealed class AlsoPingHandler(Observed observed) : IIntegrationEventHandler<Ping>
    {
        public Task HandleAsync(Ping integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            observed.AlsoPinged.Add(integrationEvent.Id);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Without_the_inbox_what_a_handler_publishes_is_committed()
    {
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(Engine, observed, compose: messaging =>
        {
            messaging.AddHandler<Relay, RelayHandler>();
            messaging.AddOutbox<ConformanceDb>(outbox => outbox.Inbox = false);
        });
        await host.StartAsync();
        var id = Guid.NewGuid();

        await host.PublishAsync(new Relay { Id = id });

        await observed.WaitForPingsAsync(1, Wait);
        var (ping, _) = Assert.Single(observed.Pings);
        Assert.Equal(id, ping.Id);
        Assert.Equal("relayed", ping.Payload);
        await host.StopAsync();
    }

    [Fact]
    public async Task Two_handlers_of_one_event_on_one_endpoint_both_run_for_each_delivery()
    {
        // As the default engine's shared queue does: one endpoint, one event, two handlers. The second joins the
        // endpoint the harness's PingHandler already drains.
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(Engine, observed, compose: messaging =>
            messaging.AddHandler<Ping, AlsoPingHandler>(GoldpathHandlerEndpoints.NameOf(typeof(PingHandler))));
        await host.StartAsync();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        foreach (var id in ids)
        {
            await host.PublishAsync(new Ping { Id = id, Payload = "both" });
        }

        await observed.WaitForPingsAsync(2, Wait);
        var deadline = DateTime.UtcNow + Wait;
        while (observed.AlsoPinged.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(ids.Order(), observed.Pings.Select(p => p.Event.Id).Order());
        Assert.Equal(ids.Order(), observed.AlsoPinged.Order());
        await host.StopAsync();
    }

    [Fact]
    public async Task The_inbox_sweep_forgets_only_rows_delivered_before_the_window_whichever_engine_wrote_them()
    {
        await using var host = await containers.BuildHostAsync(Engine, new Observed());
        var now = DateTime.UtcNow;
        var expired = Guid.NewGuid();
        var live = Guid.NewGuid();
        var unconsumed = Guid.NewGuid();
        var defaultEngines = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
            AddInboxRow(db, expired, consumed: now.AddHours(-2), expires: now.AddHours(-1));
            AddInboxRow(db, live, consumed: now.AddMinutes(-1), expires: now.AddHours(1));
            AddInboxRow(db, unconsumed, consumed: null, expires: now.AddHours(-1));   // never delivered, so kept
            AddInboxRow(db, defaultEngines, consumed: now.AddHours(-2), expires: null);   // as the default engine writes one: no expiration time
            await db.SaveChangesAsync();
        }

        var relay = ActivatorUtilities.CreateInstance<WolverineOutboxRelay<ConformanceDb>>(host.Services);
        await relay.SweepInboxAsync(CancellationToken.None);

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
            var remaining = await db.Database
                .SqlQueryRaw<Guid>("SELECT \"MessageId\" AS \"Value\" FROM \"InboxState\" WHERE \"MessageId\" IN ({0}, {1}, {2}, {3})", expired, live, unconsumed, defaultEngines)
                .ToListAsync();
            Assert.Equal(new[] { live, unconsumed }.Order(), remaining.Order());
        }
    }

    [Fact]
    public async Task A_row_this_engine_wrote_is_forgotten_by_the_sweep_once_its_delivery_is_past_the_window()
    {
        // The sweep reads Delivered, which only the delivery path stamps on a row this engine writes; the fact above
        // writes its rows by hand, so this one sweeps a row a real delivery left.
        var observed = new Observed();
        await using var host = await containers.BuildHostAsync(Engine, observed);
        await host.StartAsync();

        await host.PublishAsync(new Ping { Id = Guid.NewGuid(), Payload = "swept" });

        await observed.WaitForPingsAsync(1, Wait);
        var messageId = Assert.Single(observed.Pings).Context.MessageId!.Value;
        var deadline = DateTime.UtcNow + Wait;
        while (await InboxRowsAsync(host, messageId) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);   // the handler is observed before its transaction commits the row
        }

        Assert.Equal(1, await InboxRowsAsync(host, messageId));
        await host.StopAsync();

        var relay = ActivatorUtilities.CreateInstance<WolverineOutboxRelay<ConformanceDb>>(host.Services);
        await relay.SweepInboxAsync(CancellationToken.None);
        Assert.Equal(1, await InboxRowsAsync(host, messageId));   // still inside the window, so kept

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"InboxState\" SET \"Delivered\" = \"Delivered\" - INTERVAL '2 days' WHERE \"MessageId\" = {0}", messageId);
        }

        await relay.SweepInboxAsync(CancellationToken.None);
        Assert.Equal(0, await InboxRowsAsync(host, messageId));
    }

    private static async Task<int> InboxRowsAsync(GoldpathMessagingTestHost host, Guid messageId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
        return await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM \"InboxState\" WHERE \"MessageId\" = {0}", messageId)
            .SingleAsync();
    }

    // The inbox row's type is the engine's own; the suite names no engine (EngineFreeSuiteTests), so the row is
    // built from the context's model.
    private static void AddInboxRow(ConformanceDb db, Guid messageId, DateTime? consumed, DateTime? expires)
    {
        var entity = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "InboxState");
        var row = Activator.CreateInstance(entity.ClrType)!;
        void Set(string name, object? value) => entity.ClrType.GetProperty(name)!.SetValue(row, value);
        Set("MessageId", messageId);
        Set("ConsumerId", Guid.NewGuid());
        Set("LockId", Guid.NewGuid());
        Set("Received", consumed ?? DateTime.UtcNow);
        Set("ReceiveCount", 1);
        Set("Consumed", consumed);
        Set("Delivered", consumed);
        Set("ExpirationTime", expires);
        db.Add(row);
    }
}
