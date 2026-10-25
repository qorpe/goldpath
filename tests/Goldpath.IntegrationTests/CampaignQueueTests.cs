using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// G1 Part 3/4, S4 (RFC goldpath-messaging-engines §4.1 row 11): moving campaign onto the seam moves no
/// queue. Read from the broker itself, the operator's view: one queue named after the context, which both
/// message types' exchanges reach — the topology a fleet still running the replaced consumers declared, so a
/// rolling deployment drains one queue rather than stranding half the outcomes in a new one.
/// </summary>
public sealed class CampaignQueueTests : IAsyncLifetime
{
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    public Task InitializeAsync() => _rabbit.StartAsync();

    public Task DisposeAsync() => _rabbit.DisposeAsync().AsTask();

    /// <param name="engineTyped">
    /// The MassTransit-typed registration the templates still write until preview.11 (kept until G1 Part 4/4)
    /// must declare exactly what the builder does.
    /// </param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Campaign_on_the_seam_declares_the_queue_and_bindings_the_replaced_consumers_did(bool engineTyped)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.Services.WaitForTheBusOnStart();
        if (engineTyped)
        {
            builder.AddGoldpathMessaging(bus =>
            {
                bus.AddGoldpathCampaignConsumers<CampaignTests.CampDb>();
                bus.UsingRabbitMq((context, cfg) =>
                {
                    cfg.Host(new Uri(_rabbit.GetConnectionString()));
                    cfg.ConfigureGoldpathEndpoints(context);
                });
            });
        }
        else
        {
            builder.AddGoldpathMessaging(messaging => messaging.AddGoldpathCampaign<CampaignTests.CampDb>().UseRabbitMq());
        }

        using var host = builder.Build();
        await host.StartAsync();

        var queues = await RabbitAsync("list_queues", "name");
        var bindings = await RabbitAsync("list_bindings", "source_name", "destination_name");
        await host.StopAsync();

        Assert.Contains("camp-db", queues);
        Assert.DoesNotContain(queues, q => q.StartsWith("goldpath-campaign", StringComparison.Ordinal));   // NameOf's names: a moved queue
        Assert.Contains("Goldpath:GoldpathCampaignItemMessage\tcamp-db", bindings);
        Assert.Contains("Goldpath:GoldpathCampaignOutcomeMessage\tcamp-db", bindings);
        Assert.Contains("camp-db\tcamp-db", bindings);   // the endpoint's exchange feeds its queue
    }

    private async Task<List<string>> RabbitAsync(params string[] command)
    {
        var result = await _rabbit.ExecAsync(["rabbitmqctl", "-q", "--no-table-headers", .. command]);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }
}
