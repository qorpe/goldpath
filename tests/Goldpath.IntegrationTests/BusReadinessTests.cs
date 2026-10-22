using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The guarantee the broker-backed fixtures stand on: with <see cref="BusReadiness.WaitForTheBusOnStart"/>,
/// <c>StartAsync</c> returns with the bus started, even when the broker is slow to accept the
/// connection. Without it, MassTransit's default returns while the bus is still connecting —
/// the window in which CampaignR2Tests' first release was dropped (ci run 39734457099).
/// The broker is reached through a proxy that holds every new connection for two seconds,
/// standing in for a busy runner.
/// </summary>
public sealed class BusReadinessTests : IAsyncLifetime
{
    private static readonly TimeSpan BrokerDelay = TimeSpan.FromSeconds(2);

    public sealed record Ping(Guid Id);

    public sealed class PingConsumer : IConsumer<Ping>
    {
        public Task Consume(ConsumeContext<Ping> context) => Task.CompletedTask;
    }

    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();
    private SlowBroker _broker = null!;

    public async Task InitializeAsync()
    {
        await _rabbit.StartAsync();
        _broker = new SlowBroker(new Uri(_rabbit.GetConnectionString()), BrokerDelay);
    }

    public async Task DisposeAsync()
    {
        _broker.Dispose();
        await _rabbit.DisposeAsync();
    }

    [Fact]
    public async Task A_host_waiting_for_the_bus_returns_from_StartAsync_with_the_bus_started()
    {
        using var host = BuildHost(waitForTheBus: true);
        var clock = Stopwatch.StartNew();
        await host.StartAsync();

        Assert.Equal(BusHealthStatus.Healthy, host.Services.GetRequiredService<IBusControl>().CheckHealth().Status);
        Assert.True(clock.Elapsed >= BrokerDelay, $"StartAsync returned after {clock.Elapsed} — before the broker even answered");
        await host.StopAsync();
    }

    [Fact]
    public async Task Without_it_StartAsync_returns_while_the_bus_is_still_connecting()
    {
        // MassTransit's default, recorded: if this ever turns Healthy, the default changed and
        // WaitForTheBusOnStart is redundant — not wrong.
        using var host = BuildHost(waitForTheBus: false);
        await host.StartAsync();

        Assert.NotEqual(BusHealthStatus.Healthy, host.Services.GetRequiredService<IBusControl>().CheckHealth().Status);
        await host.StopAsync();
    }

    private IHost BuildHost(bool waitForTheBus)
    {
        var builder = Host.CreateApplicationBuilder();
        if (waitForTheBus)
        {
            builder.Services.WaitForTheBusOnStart();
        }

        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddConsumer<PingConsumer>();
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(_broker.Address);
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());
        return builder.Build();
    }

    /// <summary>A TCP proxy to the broker that holds every new connection for a fixed delay.</summary>
    private sealed class SlowBroker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public SlowBroker(Uri broker, TimeSpan delay)
        {
            _listener.Start();
            Address = new UriBuilder(broker) { Port = ((IPEndPoint)_listener.LocalEndpoint).Port }.Uri;
            _ = Task.Run(() => AcceptAsync(broker, delay));
        }

        public Uri Address { get; }

        private async Task AcceptAsync(Uri broker, TimeSpan delay)
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var upstream = new TcpClient())
                    {
                        try
                        {
                            await Task.Delay(delay, _stop.Token);
                            await upstream.ConnectAsync(broker.Host, broker.Port, _stop.Token);
                            var downstream = client.GetStream();
                            var up = upstream.GetStream();
                            await Task.WhenAny(downstream.CopyToAsync(up, _stop.Token), up.CopyToAsync(downstream, _stop.Token));
                        }
                        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException)
                        {
                            // the host or the proxy went away — the connection simply ends
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
