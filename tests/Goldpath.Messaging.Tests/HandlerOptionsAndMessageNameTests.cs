using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// RFC goldpath-messaging-engines §4.1 rows 14 and 15 (G5 Part 2/5, #331): a handler's concurrency limit and a
/// message's wire name, as the builder and the attribute accept or refuse them, and as the default engine honours
/// them in process. The broker-backed proof on both engines is the conformance suite's S2.14, S2.15 and S2.17.
/// </summary>
public class HandlerOptionsAndMessageNameTests
{
    public sealed class Parcel : IIntegrationEvent
    {
        public int Number { get; set; }
    }

    public sealed class Gauge
    {
        private int _inFlight;
        private int _peak;

        public ConcurrentQueue<int> Handled { get; } = new();

        public int Peak => Volatile.Read(ref _peak);

        public void Enter()
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }
        }

        public void Leave() => Interlocked.Decrement(ref _inFlight);
    }

    public sealed class ParcelHandler(Gauge gauge) : IIntegrationEventHandler<Parcel>
    {
        public async Task HandleAsync(Parcel integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            gauge.Enter();
            try
            {
                await Task.Delay(100, cancellationToken);
                gauge.Handled.Enqueue(integrationEvent.Number);
            }
            finally
            {
                gauge.Leave();
            }
        }
    }

    public sealed class ParcelAuditHandler : IIntegrationEventHandler<Parcel>
    {
        public Task HandleAsync(Parcel integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void A_concurrency_limit_outside_one_to_65535_is_refused_naming_the_handler(int limit)
    {
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() =>
            messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = limit }));

        Assert.Equal(nameof(GoldpathHandlerOptions.ConcurrencyLimit), refusal.ParamName);
        Assert.Contains("'ParcelHandler'", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(messaging.Handlers);   // refused before anything was recorded
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65_535)]
    public void The_boundaries_of_a_concurrency_limit_are_accepted(int limit)
    {
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());

        messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = limit });

        Assert.Equal(limit, messaging.ConcurrencyLimitOf("parcel"));
    }

    [Fact]
    public void No_limit_keeps_the_engines_default_and_an_options_object_is_required()
    {
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());

        messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions());

        Assert.Null(messaging.ConcurrencyLimitOf("parcel"));
        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => messaging.AddHandler<Parcel, ParcelHandler>(null!, "parcel")).ParamName);
        Assert.Equal("endpointName", Assert.ThrowsAny<ArgumentException>(() => messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions(), " ")).ParamName);
    }

    [Fact]
    public void A_limit_belongs_to_the_endpoint_so_every_handler_on_it_runs_under_it()
    {
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());

        messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 4 }, "parcels");
        messaging.AddHandler<Parcel, ParcelAuditHandler>("parcels");   // named no limit, joins the endpoint's
        var composition = new GoldpathMessagingComposition(messaging, waitUntilStarted: false);

        Assert.All(composition.Handlers, h => Assert.Equal(4, h.ConcurrencyLimit));
        Assert.Equal(2, composition.Handlers.Count);
    }

    [Fact]
    public void Two_limits_for_one_endpoint_or_one_pair_are_refused_and_the_same_one_twice_is_one()
    {
        var messaging = new GoldpathMessagingBuilder(new GoldpathMessagingOptions());
        messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 4 }, "parcels");

        messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 4 }, "parcels");
        var pair = Assert.Throws<InvalidOperationException>(() =>
            messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 2 }, "parcels"));
        var endpoint = Assert.Throws<InvalidOperationException>(() =>
            messaging.AddHandler<Parcel, ParcelAuditHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 2 }, "parcels"));
        var moved = Assert.Throws<InvalidOperationException>(() =>
            messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 4 }, "elsewhere"));

        Assert.Contains("ConcurrencyLimit of 4, not 2", pair.Message, StringComparison.Ordinal);
        Assert.Contains("'parcels' already runs at a ConcurrencyLimit of 4", endpoint.Message, StringComparison.Ordinal);
        Assert.Contains("'parcels'", moved.Message, StringComparison.Ordinal);
        Assert.Single(messaging.Handlers);
        Assert.Equal(4, messaging.ConcurrencyLimitOf("parcels"));
        Assert.Null(messaging.ConcurrencyLimitOf("elsewhere"));
    }

    [Fact]
    public async Task The_default_engine_runs_an_endpoint_at_its_limit_and_no_further()
    {
        var gauge = new Gauge();
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging.AddHandler<Parcel, ParcelHandler>(new GoldpathHandlerOptions { ConcurrencyLimit = 2 }),
            builder => builder.Services.AddSingleton(gauge));

        for (var i = 0; i < 12; i++)
        {
            await host.PublishAsync(new Parcel { Number = i });
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (gauge.Handled.Count < 12 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(12, gauge.Handled.Count);
        Assert.Equal(2, gauge.Peak);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("Contoso.Orders:OrderPlaced")]
    [InlineData("OrderPlaced")]
    [InlineData("Şirket.Sipariş:İadeEdildi")]   // a Turkish name: no culture is consulted, and it is an IRI-valid URN
    public void A_wire_name_an_exchange_and_a_urn_can_carry_is_kept_as_written(string name)
    {
        Assert.Equal(name, new GoldpathMessageNameAttribute(name).Name);
    }

    [Theory]
    [InlineData("urn:message:Contoso:OrderPlaced")]
    [InlineData("URN:x")]
    [InlineData("Contoso Orders:OrderPlaced")]
    [InlineData("Contoso/Orders:OrderPlaced")]
    [InlineData("Contoso:OrderPlaced?v=2")]
    [InlineData("Contoso:OrderPlaced#2")]
    [InlineData("Contoso:Order\tPlaced")]
    [InlineData("Contoso:Order\u0001Placed")]
    public void A_wire_name_an_exchange_or_a_urn_cannot_carry_is_refused(string name)
    {
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => new GoldpathMessageNameAttribute(name)).ParamName);
    }

    [Fact]
    public void A_wire_name_is_at_most_255_utf8_bytes_counted_in_bytes_not_characters()
    {
        Assert.Equal(255, new GoldpathMessageNameAttribute(new string('a', 255)).Name.Length);
        Assert.Throws<ArgumentException>(() => new GoldpathMessageNameAttribute(new string('a', 256)));

        // 'İ' is two UTF-8 bytes: 127 of them and one ASCII letter is 255 bytes, 128 of them is 256.
        _ = new GoldpathMessageNameAttribute(new string('İ', 127) + "a");
        Assert.Throws<ArgumentException>(() => new GoldpathMessageNameAttribute(new string('İ', 128)));
        Assert.Throws<ArgumentNullException>(() => new GoldpathMessageNameAttribute(null!));
        Assert.Throws<ArgumentException>(() => new GoldpathMessageNameAttribute(" "));
    }

    [GoldpathMessageName("not a name")]
    public sealed class Misnamed : IIntegrationEvent;

    public class NamedBase : IIntegrationEvent;

    [GoldpathMessageName("Contoso:Base")]
    public class NamedParent : NamedBase;

    public sealed class UnnamedChild : NamedParent;

    [Fact]
    public void The_default_engine_reads_a_declared_name_refuses_a_bad_one_naming_the_type_and_does_not_inherit_one()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => MassTransitMessageNames.Of(typeof(Misnamed)));

        Assert.Contains(typeof(Misnamed).FullName!, refusal.Message, StringComparison.Ordinal);
        Assert.Equal("Contoso:Base", MassTransitMessageNames.Of(typeof(NamedParent)));
        Assert.Null(MassTransitMessageNames.Of(typeof(UnnamedChild)));   // a derived type is a message of its own
        Assert.Null(MassTransitMessageNames.Of(typeof(Parcel)));
    }

    [Fact]
    public void The_default_engine_formats_a_named_types_exchange_by_its_name_and_any_other_as_before()
    {
        var formatter = new MassTransitMessageNames.WireEntityNameFormatter(new FixedFormatter());

        Assert.Equal("Contoso:Base", formatter.FormatEntityName<NamedParent>());
        Assert.Equal("derived", formatter.FormatEntityName<Parcel>());
    }

    private sealed class FixedFormatter : MassTransit.IEntityNameFormatter
    {
        public string FormatEntityName<T>() => "derived";
    }

    [Fact]
    public void The_culture_does_not_change_a_wire_name()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal("Contoso.Orders:ITEM", new GoldpathMessageNameAttribute("Contoso.Orders:ITEM").Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
