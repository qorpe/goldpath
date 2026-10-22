using System.Collections.Concurrent;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// THE outbox atomicity proof (deferred-ledger item from the Messaging RFC), on real
/// PostgreSQL + RabbitMQ: a rolled-back transaction publishes NOTHING; a committed one
/// delivers the event exactly once to the consumer.
/// </summary>
public sealed class OutboxAtomicityTests : IAsyncLifetime
{
    public record ShipmentRequested(Guid ShipmentId) : IIntegrationEvent;

    private sealed class Shipment
    {
        public Guid Id { get; set; }
        public string Reference { get; set; } = "";
    }

    private sealed class ShipDb(DbContextOptions<ShipDb> options) : DbContext(options)
    {
        public DbSet<Shipment> Shipments => Set<Shipment>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddInboxStateEntity();
            modelBuilder.AddOutboxMessageEntity();
            modelBuilder.AddOutboxStateEntity();
        }
    }

    private sealed class Deliveries
    {
        public ConcurrentBag<Guid> Consumed { get; } = [];
    }

    private sealed class ShipmentConsumer(Deliveries deliveries) : IConsumer<ShipmentRequested>
    {
        public Task Consume(ConsumeContext<ShipmentRequested> context)
        {
            deliveries.Consumed.Add(context.Message.ShipmentId);
            return Task.CompletedTask;
        }
    }

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder("rabbitmq:4-alpine").Build();

    public async Task InitializeAsync()
        => await Task.WhenAll(_postgres.StartAsync(), _rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        await _rabbit.DisposeAsync();
    }

    private async Task<IHost> StartHostAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<Deliveries>();
        builder.AddGoldpathData<HostApplicationBuilder, ShipDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.WaitForTheBusOnStart();   // a publish right after StartAsync must find its queue bound
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddConsumer<ShipmentConsumer>();
            bus.AddGoldpathOutbox<ShipDb>(outbox => outbox.UsePostgres());
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(_rabbit.GetConnectionString()));
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, options => options.Retry.RedeliveryIntervals.Clear());

        var host = builder.Build();
        using (var scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ShipDb>().Database.EnsureCreatedAsync();
        }

        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Rolled_back_transaction_publishes_nothing()
    {
        using var host = await StartHostAsync();
        var deliveries = host.Services.GetRequiredService<Deliveries>();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShipDb>();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Shipments.Add(new Shipment { Id = Guid.NewGuid(), Reference = "doomed" });
            await publisher.Publish(new ShipmentRequested(Guid.NewGuid()));
            await db.SaveChangesAsync();                    // outbox row written INSIDE the tx
            await transaction.RollbackAsync();              // business data + outbox die together
        }

        await Task.Delay(TimeSpan.FromSeconds(8));          // grace: delivery loop would have fired

        Assert.Empty(deliveries.Consumed);
        using var verify = host.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ShipDb>();
        Assert.Equal(0, await verifyDb.Shipments.CountAsync());
        await host.StopAsync();
    }

    [Fact]
    public async Task Committed_transaction_delivers_exactly_once()
    {
        using var host = await StartHostAsync();
        var deliveries = host.Services.GetRequiredService<Deliveries>();
        var shipmentId = Guid.NewGuid();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShipDb>();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Shipments.Add(new Shipment { Id = shipmentId, Reference = "real" });
            await publisher.Publish(new ShipmentRequested(shipmentId));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (deliveries.Consumed.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        await Task.Delay(TimeSpan.FromSeconds(5));          // grace: catch accidental duplicates

        Assert.Equal([shipmentId], deliveries.Consumed);    // exactly once, the right payload
        await host.StopAsync();
    }
}
