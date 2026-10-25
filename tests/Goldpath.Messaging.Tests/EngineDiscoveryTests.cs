using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// The engine is found, never named (RFC goldpath-messaging-engines §4.1 row 5, §9 Q2): an adapter package's
/// <see cref="GoldpathMessagingEngineAttribute"/> wins over the MassTransit engine Goldpath.Messaging still
/// carries, two adapters are refused naming both, and a declaration that is not an engine is refused in words.
/// </summary>
public class EngineDiscoveryTests
{
    private const string Floor = "Goldpath.Messaging";

    public sealed class FakeEngine : IGoldpathMessagingEngine
    {
        public string Name => "fake";

        public void Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration)
        {
        }

        public Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public sealed class OtherEngine : IGoldpathMessagingEngine
    {
        public string Name => "other";

        public void Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration)
        {
        }

        public Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    /// <summary>An engine whose only constructor is internal, as an adapter's engine type usually is.</summary>
    public sealed class InternalEngine : IGoldpathMessagingEngine
    {
        internal InternalEngine()
        {
        }

        public string Name => "internal";

        public void Compose(GoldpathMessagingComposition composition, IServiceCollection services, IConfiguration configuration)
        {
        }

        public Task PublishAsync(IServiceProvider services, Type contract, object body, GoldpathOutgoingMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    public sealed class Shipped : IIntegrationEvent
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public void With_no_adapter_the_engine_the_floor_carries_is_the_one()
    {
        var engine = GoldpathMessagingEngines.Choose([(Floor, typeof(MassTransitMessagingEngine))]);

        Assert.Equal("masstransit", engine.Name);
    }

    [Fact]
    public void An_adapter_package_wins_over_the_engine_the_floor_carries()
    {
        var engine = GoldpathMessagingEngines.Choose([(Floor, typeof(MassTransitMessagingEngine)), ("Goldpath.Messaging.Fake", typeof(FakeEngine))]);

        Assert.IsType<FakeEngine>(engine);
    }

    [Fact]
    public void Two_adapter_packages_are_refused_naming_both()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => GoldpathMessagingEngines.Choose(
            [(Floor, typeof(MassTransitMessagingEngine)), ("Goldpath.Messaging.Other", typeof(OtherEngine)), ("Goldpath.Messaging.Fake", typeof(FakeEngine))]));

        // Both, in the order of their assemblies' names, whichever order they were found in.
        Assert.Contains("referenced: 'fake' (Goldpath.Messaging.Fake), 'other' (Goldpath.Messaging.Other).", refused.Message, StringComparison.Ordinal);
        Assert.Contains("providers.messagingEngine", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void No_engine_at_all_is_refused_in_words()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => GoldpathMessagingEngines.Choose([]));

        Assert.Contains("Reference the adapter package your manifest selects", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_that_is_not_an_engine_is_refused_naming_its_assembly()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => GoldpathMessagingEngines.Choose([("Goldpath.Messaging.Broken", typeof(string))]));

        Assert.Contains("Goldpath.Messaging.Broken declares 'System.String'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_picks_one_engine_among_several_and_an_unknown_name_is_refused()
    {
        var declared = new[] { (Floor, typeof(MassTransitMessagingEngine)), ("Goldpath.Messaging.Fake", typeof(FakeEngine)) };

        Assert.Equal("masstransit", GoldpathMessagingEngines.Choose(declared, "masstransit").Name);
        Assert.Equal("fake", GoldpathMessagingEngines.Choose(declared, "fake").Name);
        var refused = Assert.Throws<InvalidOperationException>(() => GoldpathMessagingEngines.Choose(declared, "nats"));
        Assert.Contains("'nats'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_engine_with_an_internal_constructor_is_created()
    {
        Assert.Equal("internal", GoldpathMessagingEngines.Choose([("Goldpath.Messaging.Internal", typeof(InternalEngine))]).Name);
    }

    [Fact]
    public async Task The_engine_publish_refuses_a_missing_argument()
    {
        IGoldpathMessagingEngine engine = new MassTransitMessagingEngine();
        using var services = new ServiceCollection().BuildServiceProvider();
        var message = new GoldpathOutgoingMessage();

        await Assert.ThrowsAsync<ArgumentNullException>("services", () => engine.PublishAsync(null!, typeof(Shipped), new Shipped(), message, default));
        await Assert.ThrowsAsync<ArgumentNullException>("contract", () => engine.PublishAsync(services, null!, new Shipped(), message, default));
        await Assert.ThrowsAsync<ArgumentNullException>("body", () => engine.PublishAsync(services, typeof(Shipped), null!, message, default));
        await Assert.ThrowsAsync<ArgumentNullException>("message", () => engine.PublishAsync(services, typeof(Shipped), new Shipped(), null!, default));
    }

    [Fact]
    public async Task The_test_host_refuses_a_missing_body()
    {
        await using var host = GoldpathMessagingTestHost.Create(_ => { });
        await Assert.ThrowsAsync<ArgumentNullException>("body", () => host.PublishAsAsync<Shipped>(null!));
    }

    [Fact]
    public async Task An_application_does_not_wait_for_the_bus_and_the_test_host_does()
    {
        // The product default stays the engine's own (open-threads T42); only the test host waits.
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathMessaging(messaging => messaging.UseRabbitMq());
        using var app = builder.Services.BuildServiceProvider();
        Assert.False(app.GetRequiredService<IOptions<MassTransitHostOptions>>().Value.WaitUntilStarted);

        await using var test = GoldpathMessagingTestHost.Create(_ => { });
        Assert.True(test.Services.GetRequiredService<IOptions<MassTransitHostOptions>>().Value.WaitUntilStarted);
    }

    [Fact]
    public void The_tenant_a_message_carries_is_the_scope_tenant_when_the_application_registers_none()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathMessaging(messaging => messaging.UseRabbitMq());
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var holder = scope.ServiceProvider.GetRequiredService<GoldpathMessageTenantContext>();
        Assert.Same(holder, scope.ServiceProvider.GetRequiredService<ITenantContext>());
    }

    [Fact]
    public void This_process_resolves_the_engine_the_floor_carries_and_registers_it()
    {
        // Wired: the real scan of this test process finds no adapter package, so the application's
        // registration composes the MassTransit engine and exposes it to the test host.
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathMessaging(messaging => messaging.UseRabbitMq());

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal("masstransit", provider.GetRequiredService<IGoldpathMessagingEngine>().Name);
        Assert.Equal("masstransit", GoldpathMessagingEngines.Resolve().Name);
    }
}
