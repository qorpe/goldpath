using System.Collections.Concurrent;
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
/// delivers the event exactly once to the handler. Written in Goldpath's vocabulary only (RFC
/// goldpath-messaging-engines §4.3, G1 Part 2/4): the publish seam, a seam handler, the test host.
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
            modelBuilder.AddGoldpathOutboxEntities();
        }
    }

    private sealed class Deliveries
    {
        public ConcurrentBag<Guid> Consumed { get; } = [];
    }

    private sealed class ShipmentRequestedHandler(Deliveries deliveries) : IIntegrationEventHandler<ShipmentRequested>
    {
        public Task HandleAsync(ShipmentRequested integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            deliveries.Consumed.Add(integrationEvent.ShipmentId);
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

    private async Task<GoldpathMessagingTestHost> StartHostAsync()
    {
        // The test host returns from its start once the bus is up, so a publish right after it finds its queue bound.
        var host = GoldpathMessagingTestHost.Create(
            messaging =>
            {
                messaging.AddHandler<ShipmentRequested, ShipmentRequestedHandler>();
                messaging.AddOutbox<ShipDb>();
                messaging.UseRabbitMq();
                messaging.Options.Retry.RedeliveryIntervals.Clear();
            },
            builder =>
            {
                builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
                builder.Services.AddSingleton<Deliveries>();
                builder.AddGoldpathData<HostApplicationBuilder, ShipDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
            });
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
        await using var host = await StartHostAsync();
        var deliveries = host.Services.GetRequiredService<Deliveries>();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShipDb>();
            var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Shipments.Add(new Shipment { Id = Guid.NewGuid(), Reference = "doomed" });
            await publisher.PublishAsync(new ShipmentRequested(Guid.NewGuid()));
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
        await using var host = await StartHostAsync();
        var deliveries = host.Services.GetRequiredService<Deliveries>();
        var shipmentId = Guid.NewGuid();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShipDb>();
            var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();

            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Shipments.Add(new Shipment { Id = shipmentId, Reference = "real" });
            await publisher.PublishAsync(new ShipmentRequested(shipmentId));
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
