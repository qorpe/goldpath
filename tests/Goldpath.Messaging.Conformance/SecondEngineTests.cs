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

    [Fact]
    public void Two_handlers_of_one_event_on_one_endpoint_compose()
    {
        // One endpoint, one event, two handlers: both run for each delivery, as the default engine's shared queue does.
        using var host = Create(messaging =>
        {
            messaging.AddHandler<Ping, PingHandler>("pings");
            messaging.AddHandler<Ping, AlsoPingHandler>("pings");
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        });
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
