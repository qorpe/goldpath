using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// G1 Part 2/4 (RFC goldpath-messaging-engines §4.1 rows 5, 6, 8, 9 and §4.3): the registration, outbox
/// and test-host vocabulary in Goldpath's own types, composed onto the same engine the MassTransit-typed
/// overloads compose — so a handler declared on the builder receives exactly what one declared on the bus does.
/// </summary>
public class MessagingBuilderTests
{
    public record ShipmentBooked(Guid ShipmentId) : IIntegrationEvent;

    /// <summary>A contract the shaped publish can materialise property by property.</summary>
    public sealed class ShipmentDelayed : IIntegrationEvent
    {
        public Guid ShipmentId { get; set; }

        public string Reason { get; set; } = "";
    }

    public sealed class Seen
    {
        public TaskCompletionSource<(ShipmentBooked Event, IntegrationEventContext Context)> Booked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<ShipmentDelayed> Delayed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ShipmentBookedHandler(Seen seen) : IIntegrationEventHandler<ShipmentBooked>
    {
        public Task HandleAsync(ShipmentBooked integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            seen.Booked.TrySetResult((integrationEvent, context));
            return Task.CompletedTask;
        }
    }

    public sealed class ShipmentDelayedHandler(Seen seen) : IIntegrationEventHandler<ShipmentDelayed>
    {
        public Task HandleAsync(ShipmentDelayed integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            seen.Delayed.TrySetResult(integrationEvent);
            return Task.CompletedTask;
        }
    }

    public sealed class FirstDb(DbContextOptions<FirstDb> options) : DbContext(options);

    public sealed class SecondDb(DbContextOptions<SecondDb> options) : DbContext(options);

    [Fact]
    public async Task A_handler_declared_on_the_builder_receives_the_message_with_its_context()
    {
        var seen = new Seen();
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging.AddHandler<ShipmentBooked, ShipmentBookedHandler>(),
            builder => builder.Services.AddSingleton(seen));
        var messageId = Guid.NewGuid();
        var shipment = Guid.NewGuid();

        await host.PublishAsync(new ShipmentBooked(shipment), message =>
        {
            message.MessageId = messageId;
            message.Tenant = "acme";
            message.CorrelationId = "corr-7";
            message.Headers["X-Upstream"] = "erp";
        });

        var (received, context) = await seen.Booked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(shipment, received.ShipmentId);
        Assert.Equal(messageId, context.MessageId);
        Assert.Equal("acme", context.Tenant!.Value.Value);
        Assert.Equal("corr-7", context.CorrelationId);
        Assert.Equal("erp", context.Headers["X-Upstream"]);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_message_published_under_a_contract_with_another_shape_reaches_the_handler()
    {
        var seen = new Seen();
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging.AddHandlers(typeof(MessagingBuilderTests).Assembly),
            builder => builder.Services.AddSingleton(seen));
        var shipment = Guid.NewGuid();

        // A newer producer adds a field; an older one omits Reason. AddHandlers found both handlers.
        await host.PublishAsAsync<ShipmentDelayed>(new { ShipmentId = shipment, Carrier = "a field this version does not know" });

        var delayed = await seen.Delayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(shipment, delayed.ShipmentId);
        Assert.Equal("", delayed.Reason);
        await host.StopAsync();
    }

    [Fact]
    public void A_message_path_without_a_transport_is_refused_in_words()
    {
        var builder = Host.CreateApplicationBuilder();

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            builder.AddGoldpathMessaging(messaging => messaging.AddHandler<ShipmentBooked, ShipmentBookedHandler>()));

        Assert.Contains("no transport", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("UseRabbitMq()", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_outbox_on_another_context_is_refused_and_the_same_one_twice_is_one()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddOutbox<FirstDb>(o => o.Inbox = false);
            messaging.AddOutbox<FirstDb>(o => o.DuplicateDetectionWindow = TimeSpan.FromMinutes(5));
            var refusal = Assert.Throws<InvalidOperationException>(() => messaging.AddOutbox<SecondDb>());
            Assert.Contains("'FirstDb'", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("'SecondDb'", refusal.Message, StringComparison.Ordinal);
            Assert.False(messaging.Outbox!.Inbox);   // the second call on the same context configures the same outbox
            Assert.Equal(TimeSpan.FromMinutes(5), messaging.Outbox.DuplicateDetectionWindow);
            messaging.UseRabbitMq();
        });
    }

    [Fact]
    public void The_outbox_defaults_are_todays()
    {
        var options = new GoldpathOutboxOptions();

        Assert.Equal(TimeSpan.FromMinutes(30), options.DuplicateDetectionWindow);
        Assert.True(options.Inbox);
        Assert.Null(options.DeliveryPollInterval);
    }

    [Fact]
    public void Options_bind_from_configuration_first_and_the_builder_wins()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Goldpath:Messaging:Retry:ImmediateCount"] = "7",
        });
        var bound = 0;

        builder.AddGoldpathMessaging(messaging =>
        {
            bound = messaging.Options.Retry.ImmediateCount;
            messaging.Options.Retry.ImmediateCount = 1;
            messaging.Options.Retry.RedeliveryIntervals.Clear();
            messaging.UseRabbitMq();
        });

        Assert.Equal(7, bound);
        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<GoldpathMessagingOptions>();
        Assert.Equal(1, options.Retry.ImmediateCount);
        Assert.Empty(options.Retry.RedeliveryIntervals);
    }

    [Fact]
    public void A_transport_cannot_be_chosen_twice()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.UseRabbitMq().UseRabbitMq("broker");
            Assert.Equal("broker", messaging.RabbitMqConnectionName);
            var refusal = Assert.Throws<InvalidOperationException>(() => messaging.UseInMemory());
            Assert.Contains("one transport", refusal.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0, null, "DuplicateDetectionWindow")]
    [InlineData(-1, null, "DuplicateDetectionWindow")]
    [InlineData(30, 0, "DeliveryPollInterval")]
    [InlineData(30, -5, "DeliveryPollInterval")]
    public void An_outbox_interval_that_is_not_positive_is_refused_at_registration(int windowMinutes, int? pollSeconds, string option)
    {
        var builder = Host.CreateApplicationBuilder();

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddOutbox<FirstDb>(o =>
            {
                o.DuplicateDetectionWindow = TimeSpan.FromMinutes(windowMinutes);
                o.DeliveryPollInterval = pollSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
            });
            messaging.UseRabbitMq();
        }));

        Assert.Equal(option, refusal.ParamName);
    }

    [Fact]
    public async Task A_host_whose_outbox_context_has_no_lock_statements_refuses_to_start_naming_the_provider()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await using var host = GoldpathMessagingTestHost.Create(
            messaging => messaging.AddOutbox<FirstDb>(),
            builder => builder.Services.AddDbContext<FirstDb>(o => o.UseSqlite(connection)));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("'FirstDb'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'Microsoft.EntityFrameworkCore.Sqlite'", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "postgres")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "sqlServer")]
    public void The_outbox_lock_statements_follow_the_contexts_provider(string provider, string expected)
    {
        var postgres = new PostgresLockStatementProvider();
        var sqlServer = new SqlServerLockStatementProvider();
        var statements = new MassTransitMessagingEngine.ProviderLockStatements(postgres, sqlServer);

        Assert.Same(expected == "postgres" ? postgres : sqlServer, statements.For(provider, "ShopDb"));
    }

    [Fact]
    public void An_outbox_on_a_provider_with_no_lock_statements_is_refused_naming_it()
    {
        var statements = new MassTransitMessagingEngine.ProviderLockStatements(new PostgresLockStatementProvider(), new SqlServerLockStatementProvider());

        var refusal = Assert.Throws<InvalidOperationException>(() => statements.For("Microsoft.EntityFrameworkCore.Sqlite", "ShopDb"));

        Assert.Contains("'ShopDb'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'Microsoft.EntityFrameworkCore.Sqlite'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_goldpath_outbox_entities_are_the_tables_and_columns_the_three_engine_calls_map()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        using var engineNamed = new OutboxDb(Options(connection), goldpath: false);
        using var goldpath = new OutboxDb(Options(connection), goldpath: true);

        Assert.Equal(Shape(engineNamed), Shape(goldpath));
        Assert.Equal(["InboxState", "OutboxMessage", "OutboxState"], Shape(goldpath).Select(t => t.Split(':')[0]).Distinct().Order());
    }

    private static DbContextOptions<OutboxDb> Options(SqliteConnection connection)
        => new DbContextOptionsBuilder<OutboxDb>().UseSqlite(connection)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, PerShapeCacheKey>().Options;

    private static List<string> Shape(DbContext context)
        => [.. context.Model.GetRelationalModel().Tables
            .SelectMany(t => t.Columns.Select(c => $"{t.Name}:{c.Name}:{c.StoreType}:{c.IsNullable}"))
            .Order(StringComparer.Ordinal)];

    public sealed class OutboxDb(DbContextOptions<OutboxDb> options, bool goldpath) : DbContext(options)
    {
        public bool Goldpath { get; } = goldpath;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (Goldpath)
            {
                modelBuilder.AddGoldpathOutboxEntities();
            }
            else
            {
                modelBuilder.AddInboxStateEntity();
                modelBuilder.AddOutboxMessageEntity();
                modelBuilder.AddOutboxStateEntity();
            }
        }
    }

    private sealed class PerShapeCacheKey : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) => (context.GetType(), ((OutboxDb)context).Goldpath, designTime);
    }
}
