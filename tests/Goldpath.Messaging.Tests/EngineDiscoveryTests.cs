using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            [(Floor, typeof(MassTransitMessagingEngine)), ("Goldpath.Messaging.Fake", typeof(FakeEngine)), ("Goldpath.Messaging.Other", typeof(OtherEngine))]));

        Assert.Contains("'fake' (Goldpath.Messaging.Fake)", refused.Message, StringComparison.Ordinal);
        Assert.Contains("'other' (Goldpath.Messaging.Other)", refused.Message, StringComparison.Ordinal);
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
