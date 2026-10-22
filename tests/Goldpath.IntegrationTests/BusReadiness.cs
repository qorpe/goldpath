using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath.Tests.Integration;

/// <summary>
/// MassTransit's hosted service returns from <c>StartAsync</c> at once and connects to the
/// broker in the background. A test that publishes right after <c>StartAsync</c> then races
/// its own receive endpoints: on a fresh broker the queue is not yet bound to the message's
/// exchange, and RabbitMQ drops a message no queue is bound for — silently. The campaign
/// leader releases inside its first tick, so CampaignR2Tests lost its whole 40-item batch
/// that way on a slow runner (ci run 39734457099) and timed out before the orphan sweep
/// (5 minutes) could publish it again. A host built with <see cref="WaitForTheBusOnStart"/>
/// returns from <c>StartAsync</c> with every receive endpoint started.
/// </summary>
public static class BusReadiness
{
    /// <summary>Makes <c>StartAsync</c> wait until the bus has started (BusReadinessTests proves it).</summary>
    public static IServiceCollection WaitForTheBusOnStart(this IServiceCollection services)
        => services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
}
