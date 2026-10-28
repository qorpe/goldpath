using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// What referencing the second engine's adapter means, without a broker: it is the engine this process finds
/// (RFC goldpath-messaging-engines §9 Q2: an adapter package wins over the engine Goldpath.Messaging carries),
/// it names the 8.x exchanges the default engine names (§4.4), and it refuses, in words, a composition it
/// cannot honour yet rather than running it differently (open-threads T18).
/// </summary>
public sealed class SecondEngineTests
{
    private const string Engine = "wolverine";

    public sealed class Settled : IIntegrationEvent
    {
        public Guid Id { get; set; }

        public sealed class Nested : IIntegrationEvent
        {
            public Guid Id { get; set; }
        }
    }

    public sealed class Generic<T> : IIntegrationEvent
    {
        public T? Value { get; set; }
    }

    public sealed class SettledHandler : IIntegrationEventHandler<Settled>
    {
        public Task HandleAsync(Settled integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class AlsoPingHandler : IIntegrationEventHandler<Ping>
    {
        public Task HandleAsync(Ping integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void A_process_that_references_the_adapter_runs_on_it_without_naming_it()
    {
        Assert.Equal(Engine, GoldpathMessagingEngines.Resolve().Name);
        Assert.Equal("masstransit", GoldpathMessagingEngines.Resolve("masstransit").Name);
    }

    [Fact]
    public void The_exchange_and_urn_are_the_8x_names()
    {
        Assert.Equal("Goldpath.Messaging.Conformance:Ping", GoldpathWire.ExchangeOf(typeof(Ping)));
        Assert.Equal("urn:message:Goldpath.Messaging.Conformance:Ping", GoldpathWire.UrnOf(typeof(Ping)));
        Assert.Equal("Goldpath.Messaging.Conformance:SecondEngineTests+Settled+Nested", GoldpathWire.ExchangeOf(typeof(Settled.Nested)));
        Assert.Equal("Goldpath.Messaging.Conformance:Ping", GoldpathWire.ExchangeOfUrn("urn:message:Goldpath.Messaging.Conformance:Ping"));
        Assert.Throws<FormatException>(() => GoldpathWire.ExchangeOfUrn("Goldpath.Messaging.Conformance:Ping"));
        Assert.Throws<NotSupportedException>(() => GoldpathWire.ExchangeOf(typeof(Generic<int>)));
    }

    [Fact]
    public void An_outbox_row_is_relayed_to_its_own_types_exchange_not_to_the_list_of_its_types()
    {
        // The default engine's MessageType column, as 8.5.11 writes it for an event that implements IIntegrationEvent.
        Assert.Equal("Goldpath.Messaging.Conformance:Ping",
            GoldpathWire.ExchangeOfMessageTypes("urn:message:Goldpath.Messaging.Conformance:Ping;urn:message:Goldpath:IIntegrationEvent"));
        Assert.Equal("Goldpath.Messaging.Conformance:Ping", GoldpathWire.ExchangeOfMessageTypes("urn:message:Goldpath.Messaging.Conformance:Ping"));
        Assert.Throws<FormatException>(() => GoldpathWire.ExchangeOfMessageTypes("Goldpath.Messaging.Conformance:Ping;urn:message:Goldpath:IIntegrationEvent"));
    }

    [Fact]
    public void The_inbox_keys_a_handler_as_the_default_engine_does()
    {
        // Each id is the one MassTransit 8.5.11 derives for the default engine's adapter consumer of that handler on
        // that queue, read from its own type-name formatter and hash: Ping's from an InboxState row 8.5.11 wrote.
        Assert.Equal(Guid.Parse("866a0036-11ef-81e3-1bbe-9d5edb4c7579"), GoldpathInboxKey.ConsumerIdOf(typeof(Ping), typeof(PingHandler), "ping"));
        Assert.Equal(Guid.Parse("9b297fd6-cca2-f960-7677-bf239d645e5d"), GoldpathInboxKey.ConsumerIdOf(typeof(Ping), typeof(WolverineAdapterTests.AlsoPingHandler), "ping"));
        Assert.Equal(Guid.Parse("ad5781ca-1bd3-5cf0-74e8-ce268cf2d752"), GoldpathInboxKey.ConsumerIdOf(typeof(Settled), typeof(SettledHandler), "settled"));

        // One row per handler, and per queue: a second handler or another queue is another consumer.
        Assert.NotEqual(GoldpathInboxKey.ConsumerIdOf(typeof(Ping), typeof(PingHandler), "ping"), GoldpathInboxKey.ConsumerIdOf(typeof(Ping), typeof(PingHandler), "pong"));
    }

    [Fact]
    public void A_short_type_name_is_written_as_the_default_engine_writes_it()
    {
        // 8.5.11's own output for each type: a generic argument drops the namespace it shares with its generic type.
        Assert.Equal("Goldpath.IntegrationEvent<Goldpath.Messaging.Conformance.Ping>", GoldpathInboxKey.ShortNameOf(typeof(IntegrationEvent<Ping>)));
        Assert.Equal("System.Collections.Generic.Dictionary<System.String,List<Goldpath.GoldpathOutgoingMessage>>",
            GoldpathInboxKey.ShortNameOf(typeof(Dictionary<string, List<GoldpathOutgoingMessage>>)));
        Assert.Equal("Goldpath.Messaging.Conformance.SecondEngineTests+Settled+Nested", GoldpathInboxKey.ShortNameOf(typeof(Settled.Nested)));
        Assert.Throws<ArgumentException>(() => GoldpathInboxKey.ShortNameOf(typeof(Generic<>)));
    }

    [Fact]
    public void The_envelope_names_the_broker_without_its_credentials()
    {
        var wire = new GoldpathWire(new Uri("amqp://app-user:s3cret-pass@broker.internal:5673/fleet"));
        var envelope = System.Text.Encoding.UTF8.GetString(wire.Envelope(
            typeof(Ping), new Ping { Id = Guid.NewGuid() }, Guid.NewGuid(), new Dictionary<string, string>(), DateTimeOffset.UtcNow));

        Assert.DoesNotContain("s3cret-pass", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("app-user", envelope, StringComparison.Ordinal);
        Assert.Contains("\"destinationAddress\":\"rabbitmq://broker.internal:5673/fleet/Goldpath.Messaging.Conformance:Ping\"", envelope, StringComparison.Ordinal);
        Assert.Equal("rabbitmq://broker.internal:5673/fleet", wire.Address.ToString());
        Assert.Equal("rabbitmq://localhost/orders", new GoldpathWire(new Uri("amqp://guest:guest@localhost:5672/")).AddressOf("orders").ToString());
    }

    [Fact]
    public void Delayed_redelivery_is_refused_until_it_is_durable()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Create(messaging =>
        {
            messaging.AddHandler<Settled, SettledHandler>();
            messaging.UseRabbitMq();
        }));

        Assert.Contains("RedeliveryIntervals", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_in_memory_transport_is_refused()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Create(messaging =>
        {
            messaging.AddHandler<Settled, SettledHandler>();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        }));

        Assert.Contains("runs on RabbitMQ", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_shared_by_two_event_types_is_refused_naming_both()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Create(messaging =>
        {
            messaging.AddHandler<Settled, SettledHandler>("shared");
            messaging.AddHandler<Ping, AlsoPingHandler>("shared");
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        }));

        Assert.Contains("'shared' takes Settled, Ping", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_drained_singly_and_in_batches_is_refused()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Create(messaging =>
        {
            messaging.AddHandler<Tick, TickSingleHandler>("ticks");
            messaging.AddBatchHandler<Tick, TickBatchHandler>(new GoldpathBatchOptions(), "ticks");
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        }));

        Assert.Contains("'ticks'", refused.Message, StringComparison.Ordinal);
    }

    private static DisposableHost Create(Action<GoldpathMessagingBuilder> configure)
        => new(GoldpathMessagingTestHost.Create(configure, builder => builder.Configuration["ConnectionStrings:messaging"] = "amqp://guest:guest@localhost:5672/", Engine));

    private sealed class DisposableHost(GoldpathMessagingTestHost host) : IDisposable
    {
        public void Dispose() => host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public sealed class TickSingleHandler : IIntegrationEventHandler<Tick>
    {
        public Task HandleAsync(Tick integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
