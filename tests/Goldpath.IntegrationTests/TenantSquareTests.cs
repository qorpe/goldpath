using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// THE full-square proof (MultiTenancy RFC D6): an HTTP request's tenant survives every
/// seam on real infrastructure — middleware resolution → EF stamp → outbox publish →
/// RabbitMQ → consume restoration → a second EF stamp in the consumer. Both rows end up
/// owned by the ORIGIN tenant, and stay invisible without one. Written in Goldpath's vocabulary
/// only (RFC goldpath-messaging-engines §4.1, G1 Part 2/4): the builder, the publish seam, a seam handler.
/// </summary>
public sealed class TenantSquareTests : IAsyncLifetime
{
    public record OrderPlaced(Guid OrderId) : IIntegrationEvent;

    private sealed class Order : IMultiTenant
    {
        public Guid Id { get; set; }
        public TenantId TenantId { get; set; }
    }

    private sealed class Invoice : IMultiTenant
    {
        public Guid Id { get; set; }
        public Guid OrderId { get; set; }
        public TenantId TenantId { get; set; }
    }

    private sealed class OrderDb(DbContextOptions<OrderDb> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<Invoice> Invoices => Set<Invoice>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyGoldpathMultiTenancy(this);
            modelBuilder.AddGoldpathOutboxEntities();
        }
    }

    private sealed class InvoiceOnOrderPlacedHandler(OrderDb db) : IIntegrationEventHandler<OrderPlaced>
    {
        public async Task HandleAsync(OrderPlaced integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            // No explicit tenant anywhere: the stamp comes from the ambient flow the
            // consume pipeline restored from the message headers.
            db.Invoices.Add(new Invoice { Id = Guid.NewGuid(), OrderId = integrationEvent.OrderId });
            await db.SaveChangesAsync(cancellationToken);
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

    [Fact]
    public async Task Http_tenant_survives_middleware_stamp_outbox_broker_and_consumer_stamp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddGoldpathMultiTenancy();
        builder.AddGoldpathData<WebApplicationBuilder, OrderDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.WaitForTheBusOnStart();   // a publish right after StartAsync must find its queue bound
        builder.Configuration["ConnectionStrings:messaging"] = _rabbit.GetConnectionString();
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddHandler<OrderPlaced, InvoiceOnOrderPlacedHandler>();
            messaging.AddOutbox<OrderDb>();
            messaging.UseRabbitMq();
            messaging.Options.Retry.RedeliveryIntervals.Clear();
        });

        await using var app = builder.Build();
        app.UseGoldpathMultiTenancy();
        app.MapPost("/orders", async (OrderDb db, IIntegrationEventPublisher publisher) =>
        {
            // No explicit tenant here either — middleware resolved it from the header.
            var order = new Order { Id = Guid.NewGuid() };
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Orders.Add(order);
            await publisher.PublishAsync(new OrderPlaced(order.Id));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return Results.Ok(order.Id);
        });

        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderDb>().Database.EnsureCreatedAsync();
        }

        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(GoldpathHeaders.TenantId, "acme");

        var response = await client.PostAsync("/orders", content: null);
        response.EnsureSuccessStatusCode();

        // Wait for the outbox → broker → consumer loop to land the invoice.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        using var verify = app.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<OrderDb>();
        using (GoldpathTenant.Use("acme"))
        {
            while (!await db.Invoices.AnyAsync() && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            var order = await db.Orders.SingleAsync();
            var invoice = await db.Invoices.SingleAsync();
            Assert.Equal("acme", order.TenantId.Value);      // stamped on the HTTP seam
            Assert.Equal("acme", invoice.TenantId.Value);    // stamped on the CONSUMER seam
            Assert.Equal(order.Id, invoice.OrderId);
        }

        // The square stays closed: no ambient tenant, no data.
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.Invoices.CountAsync());

        // And a foreign tenant sees nothing of acme's.
        using (GoldpathTenant.Use("globex"))
        {
            Assert.Equal(0, await db.Orders.CountAsync());
        }

        await app.StopAsync();
    }
}
