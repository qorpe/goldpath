using System.Text.Json.Nodes;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// #458: the message-size floor. The conformance facts S2.20 and S2.21 prove it on both engines on real RabbitMQ and
/// PostgreSQL; these pin its boundaries, its wiring and its sweep without a broker, so the gates job (and its mutation
/// run) can score them.
/// </summary>
public sealed class MessageSizeTests : IDisposable
{
    private const int Max = 300;
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public MessageSizeTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    public sealed record Sized(string Payload) : IIntegrationEvent;

    public sealed class SizeDb(DbContextOptions<SizeDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathOutboxEntities();
    }

    /// <summary>Stands in for an engine: it adds the event's outbox row to the caller's context, as both adapters do.</summary>
    private sealed class RowWritingPublisher(SizeDb db, string body) : IIntegrationEventPublisher
    {
        public Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            db.Set<OutboxMessage>().Add(Message(body, Guid.NewGuid()));
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_that_is_not_positive_is_refused_at_registration(int max)
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging =>
        {
            messaging.Options.MaxMessageBytes = max;
            messaging.UseRabbitMq();
        }));

        Assert.Equal(nameof(GoldpathMessagingOptions.MaxMessageBytes), refusal.ParamName);
    }

    [Fact]
    public async Task The_default_is_RabbitMQ_4s_max_message_size_and_configuration_overrides_it()
    {
        Assert.Equal(16 * 1024 * 1024, new GoldpathMessagingOptions().MaxMessageBytes);

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Goldpath:Messaging:MaxMessageBytes"] = "134217728";
        builder.AddGoldpathMessaging(messaging => messaging.UseRabbitMq());
        await using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(134_217_728, services.GetRequiredService<GoldpathMessagingOptions>().MaxMessageBytes);
    }

    [Fact]
    public async Task With_an_outbox_the_publisher_is_guarded_and_the_sweep_is_the_first_hosted_service()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbContext<SizeDb>(o => o.UseSqlite(_connection));
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.AddOutbox<SizeDb>();
            messaging.UseRabbitMq();
        });

        var hosted = builder.Services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        await using var services = builder.Services.BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        Assert.IsType<GoldpathSizeGuardPublisher>(scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>());
        Assert.IsType<GoldpathOutboxOversizeSweeper<SizeDb>>(hosted[0].ImplementationFactory!(services));
        Assert.Single(builder.Services, d => d.ServiceType == typeof(IIntegrationEventPublisher));
        Assert.Single(builder.Services, IsSweeper);
    }

    // The sweep's registration is the size floor's own factory; nothing else registers one.
    private static bool IsSweeper(ServiceDescriptor d)
        => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory?.Method.DeclaringType?.DeclaringType == typeof(GoldpathOutboxSize);

    [Fact]
    public void Without_an_outbox_nothing_is_wrapped_and_nothing_sweeps()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddGoldpathMessaging(messaging => messaging.UseRabbitMq());

        var publisher = Assert.Single(builder.Services, d => d.ServiceType == typeof(IIntegrationEventPublisher));
        Assert.Null(publisher.ImplementationFactory);
        Assert.DoesNotContain(builder.Services, IsSweeper);
    }

    // 300 ASCII bytes is the limit itself; 'İ' (tr-TR's dotted capital I) is two UTF-8 bytes, '€' three, '😀' four in
    // two UTF-16 characters. The character pre-filter must never let a body over the limit in UTF-8 bytes through.
    [Theory]
    [InlineData("a", 300, false)]
    [InlineData("a", 301, true)]
    [InlineData("İ", 150, false)]
    [InlineData("İ", 151, true)]
    [InlineData("€", 100, false)]
    [InlineData("€", 101, true)]
    [InlineData("😀", 75, false)]
    [InlineData("😀", 76, true)]
    public async Task A_publish_is_refused_exactly_above_the_limit_in_UTF8_bytes(string unit, int repeat, bool refused)
    {
        var body = string.Concat(Enumerable.Repeat(unit, repeat));
        await using var db = await DbAsync();
        var guard = new GoldpathSizeGuardPublisher(new RowWritingPublisher(db, body), db, new GoldpathMessagingOptions { MaxMessageBytes = Max });

        if (!refused)
        {
            await guard.PublishAsync(new Sized("x"));
            Assert.Single(db.ChangeTracker.Entries<OutboxMessage>(), e => e.State == EntityState.Added);
            return;
        }

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.PublishAsync(new Sized("x")));
        Assert.Contains(nameof(Sized), refusal.Message);
        Assert.Contains($"{System.Text.Encoding.UTF8.GetByteCount(body)} bytes", refusal.Message);
        Assert.Contains("MaxMessageBytes (300)", refusal.Message);
        Assert.DoesNotContain(unit + unit + unit, refusal.Message);   // never the payload
        Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());      // out of the unit of work: a later save cannot commit it
        await db.SaveChangesAsync();
        Assert.Equal(0, await db.Set<OutboxMessage>().CountAsync());
    }

    [Fact]
    public async Task A_refusal_leaves_the_rows_already_in_the_unit_of_work_alone()
    {
        await using var db = await DbAsync();
        db.Set<OutboxMessage>().Add(Message("small", Guid.NewGuid()));
        var guard = new GoldpathSizeGuardPublisher(new RowWritingPublisher(db, new string('a', Max + 1)), db, new GoldpathMessagingOptions { MaxMessageBytes = Max });

        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.PublishAsync(new Sized("x")));

        Assert.Equal("small", Assert.Single(db.ChangeTracker.Entries<OutboxMessage>()).Entity.Body);
    }

    [Fact]
    public async Task The_sweep_marks_an_attached_row_above_the_limit_failed_in_place_and_nothing_else()
    {
        var outbox = Guid.NewGuid();
        var inboxMessage = Guid.NewGuid();
        var inboxConsumer = Guid.NewGuid();
        await using (var db = await DbAsync())
        {
            db.Set<OutboxState>().Add(new OutboxState { OutboxId = outbox, LockId = Guid.NewGuid(), Created = DateTime.UtcNow });
            db.Set<InboxState>().Add(new InboxState { MessageId = inboxMessage, ConsumerId = inboxConsumer, LockId = Guid.NewGuid(), Received = DateTime.UtcNow, ReceiveCount = 1 });
            // Twelve long rows under the limit come FIRST: more than one page, so the walk must reach the oversized row behind them.
            for (var i = 0; i < 12; i++)
            {
                db.Set<OutboxMessage>().Add(Message(new string('a', Max), outbox, headers: "{\"keep\":\"me\"}"));
                await db.SaveChangesAsync();   // one at a time: the sequence numbers follow this order
            }

            db.Set<OutboxMessage>().Add(Message(new string('İ', 151), outbox, headers: "{\"MT-Activity-Id\":\"00-abc\"}"));
            await db.SaveChangesAsync();
            var fromConsumer = Message(new string('b', Max + 1), outboxId: null);
            fromConsumer.InboxMessageId = inboxMessage;
            fromConsumer.InboxConsumerId = inboxConsumer;
            db.Set<OutboxMessage>().Add(fromConsumer);
            await db.SaveChangesAsync();
            db.Set<OutboxMessage>().Add(Message(new string('c', Max + 1), outboxId: null));   // already detached: not attached to anything
            await db.SaveChangesAsync();
        }

        var sweeper = Sweeper();
        Assert.Equal(2, await sweeper.SweepAsync(CancellationToken.None));
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));   // re-runnable: a marked row is never taken again

        await using var read = await DbAsync(create: false);
        var rows = await read.Set<OutboxMessage>().OrderBy(m => m.SequenceNumber).ToListAsync();
        Assert.All(rows.Take(12), r => Assert.Equal((outbox, "{\"keep\":\"me\"}"), (r.OutboxId!.Value, r.Headers)));

        var turkish = JsonNode.Parse(rows[12].Headers!)!.AsObject();
        Assert.Null(rows[12].OutboxId);
        Assert.Equal("00-abc", (string?)turkish["MT-Activity-Id"]);
        Assert.Equal(outbox.ToString(), (string?)turkish[GoldpathOutboxSize.FailedOutboxIdHeader]);
        Assert.StartsWith("302 bytes exceeds MaxMessageBytes 300", (string?)turkish[GoldpathOutboxSize.FailedHeader]);
        Assert.Equal(new string('İ', 151), rows[12].Body);   // kept in place, body and all

        var consumer = JsonNode.Parse(rows[13].Headers!)!.AsObject();
        Assert.Equal((null, null, (Guid?)null), (rows[13].InboxMessageId, rows[13].InboxConsumerId, rows[13].OutboxId));
        Assert.Equal($"{inboxMessage}/{inboxConsumer}", (string?)consumer[GoldpathOutboxSize.FailedInboxHeader]);

        Assert.Null(rows[14].Headers);   // the row that was already detached is not touched
    }

    [Fact]
    public async Task The_sweep_reads_only_the_oldest_undelivered_outboxes_and_reaches_a_row_once_it_is_at_the_head()
    {
        // Both delivery loops take the oldest undelivered outbox first, so that is the only place a row holds others
        // back; a pass that measured every body of a backlog would run once a second over all of it.
        var states = Enumerable.Range(0, 11).Select(i => new OutboxState
        {
            OutboxId = Guid.NewGuid(),
            LockId = Guid.NewGuid(),
            Created = DateTime.UtcNow.AddMinutes(-60 + i),
        }).ToList();
        await using (var db = await DbAsync())
        {
            db.Set<OutboxState>().AddRange(states);
            db.Set<OutboxMessage>().Add(Message(new string('a', Max + 1), states[^1].OutboxId));   // the newest: outside the head
            await db.SaveChangesAsync();
        }

        var sweeper = Sweeper();
        Assert.Equal(0, await sweeper.SweepAsync(CancellationToken.None));

        await using (var db = await DbAsync(create: false))
        {
            var oldest = await db.Set<OutboxState>().SingleAsync(s => s.OutboxId == states[0].OutboxId);
            oldest.Delivered = DateTime.UtcNow;   // delivered: the oversized row's outbox is now within the head
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await sweeper.SweepAsync(CancellationToken.None));
    }

    private GoldpathOutboxOversizeSweeper<SizeDb> Sweeper()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SizeDb>(o => o.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();
        return new GoldpathOutboxOversizeSweeper<SizeDb>(
            provider.GetRequiredService<IServiceScopeFactory>(), new GoldpathMessagingOptions { MaxMessageBytes = Max },
            new GoldpathOutboxOptions(), NullLogger<GoldpathOutboxOversizeSweeper<SizeDb>>.Instance);
    }

    private async Task<SizeDb> DbAsync(bool create = true)
    {
        var db = new SizeDb(new DbContextOptionsBuilder<SizeDb>().UseSqlite(_connection).Options);
        if (create)
        {
            await db.Database.EnsureCreatedAsync();
        }

        return db;
    }

    private static OutboxMessage Message(string body, Guid? outboxId, string? headers = null) => new()
    {
        OutboxId = outboxId,
        MessageId = Guid.NewGuid(),
        ContentType = "application/vnd.masstransit+json",
        MessageType = "urn:message:Goldpath.Tests:Sized",
        Body = body,
        Headers = headers,
        SentTime = DateTime.UtcNow,
    };
}
