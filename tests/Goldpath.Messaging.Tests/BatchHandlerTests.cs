using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// G1 Part 3/4 (RFC goldpath-messaging-engines §4.1 row 12): the batch half of the consume seam, and the
/// named-endpoint registration that lets a queue keep the name a running fleet already drains.
/// </summary>
public class BatchHandlerTests
{
    public sealed record Reading(int Number) : IIntegrationEvent;

    public sealed record Alarm(int Number) : IIntegrationEvent;

    public sealed class Seen
    {
        public ConcurrentQueue<IReadOnlyList<IntegrationEvent<Reading>>> Batches { get; } = new();

        public ConcurrentQueue<Alarm> Alarms { get; } = new();

        /// <summary>How many more calls throw before the handler starts taking batches.</summary>
        public int ThrowFirst;

        public int Delivered => Batches.Sum(b => b.Count);

        public async Task WaitAsync(Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }

    public sealed class ReadingBatchHandler(Seen seen) : IIntegrationEventBatchHandler<Reading>
    {
        public Task HandleAsync(IReadOnlyList<IntegrationEvent<Reading>> batch, CancellationToken cancellationToken = default)
        {
            seen.Batches.Enqueue(batch);
            if (Interlocked.Decrement(ref seen.ThrowFirst) >= 0)
            {
                throw new InvalidOperationException("the sink's database blinked");
            }

            return Task.CompletedTask;
        }
    }

    public sealed class AlarmHandler(Seen seen) : IIntegrationEventHandler<Alarm>
    {
        public Task HandleAsync(Alarm integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
        {
            seen.Alarms.Enqueue(integrationEvent);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Each_delivery_in_a_batch_carries_its_own_identity_tenant_correlation_and_headers()
    {
        var seen = new Seen();
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging.AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions { MessageLimit = 10, TimeLimit = TimeSpan.FromMilliseconds(200) }),
            builder => builder.Services.AddSingleton(seen));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await host.PublishAsync(new Reading(1), m => { m.MessageId = first; m.Tenant = "acme"; m.CorrelationId = "corr-1"; m.Headers["X-Upstream"] = "meter"; });
        await host.PublishAsync(new Reading(2), m => { m.MessageId = second; m.Tenant = "globex"; m.CorrelationId = "corr-2"; });
        await seen.WaitAsync(() => seen.Delivered >= 2);

        var deliveries = seen.Batches.SelectMany(b => b).OrderBy(e => e.Message.Number).ToList();
        Assert.Equal([1, 2], deliveries.Select(e => e.Message.Number));
        Assert.Equal(first, deliveries[0].Context.MessageId);
        Assert.Equal("acme", deliveries[0].Context.Tenant!.Value.Value);
        Assert.Equal("corr-1", deliveries[0].Context.CorrelationId);
        Assert.Equal("meter", deliveries[0].Context.Headers["X-Upstream"]);
        Assert.Equal(0, deliveries[0].Context.RetryAttempt);
        Assert.Equal(second, deliveries[1].Context.MessageId);
        Assert.Equal("globex", deliveries[1].Context.Tenant!.Value.Value);   // one batch, two tenants: each keeps its own
        Assert.Equal("corr-2", deliveries[1].Context.CorrelationId);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_throw_fails_the_whole_batch_into_the_retry_ladder()
    {
        var seen = new Seen { ThrowFirst = 1 };
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging.AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions { MessageLimit = 3, TimeLimit = TimeSpan.FromSeconds(5) }),
            builder => builder.Services.AddSingleton(seen));

        for (var i = 1; i <= 3; i++)
        {
            await host.PublishAsync(new Reading(i));
        }

        await seen.WaitAsync(() => seen.Batches.Count >= 2);

        var calls = seen.Batches.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal([1, 2, 3], calls[0].Select(e => e.Message.Number).Order());
        Assert.Equal([1, 2, 3], calls[1].Select(e => e.Message.Number).Order());   // the same three, not the two the throw left behind
        Assert.All(calls[1], e => Assert.Equal(1, e.Context.RetryAttempt));
        await host.StopAsync();
    }

    [Fact]
    public async Task Handlers_given_one_endpoint_name_share_one_queue_and_both_receive()
    {
        var seen = new Seen();
        await using var host = await GoldpathMessagingTestHost.StartAsync(
            messaging => messaging
                .AddHandler<Alarm, AlarmHandler>("fleet-db")
                .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions { TimeLimit = TimeSpan.FromMilliseconds(100) }, "fleet-db"),
            builder => builder.Services.AddSingleton(seen));

        await host.PublishAsync(new Alarm(7));
        await host.PublishAsync(new Reading(8));
        await seen.WaitAsync(() => !seen.Alarms.IsEmpty && seen.Delivered >= 1);

        Assert.Equal(7, Assert.Single(seen.Alarms).Number);
        Assert.Equal(8, Assert.Single(seen.Batches.SelectMany(b => b)).Message.Number);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(0, 1000, 1, "MessageLimit")]
    [InlineData(-3, 1000, 1, "MessageLimit")]
    [InlineData(10, 0, 1, "TimeLimit")]
    [InlineData(10, -1, 1, "TimeLimit")]
    [InlineData(10, 1000, 0, "ConcurrencyLimit")]
    [InlineData(65_536, 1000, 1, "MessageLimit")]
    [InlineData(int.MaxValue, 1000, 2, "MessageLimit")]   // the product overflows int; refused, not wrapped
    [InlineData(256, 1000, 256, "MessageLimit")]
    public void A_grouping_no_engine_can_honour_is_refused_at_registration(int messages, int milliseconds, int concurrency, string option)
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging => messaging
            .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions
            {
                MessageLimit = messages,
                TimeLimit = TimeSpan.FromMilliseconds(milliseconds),
                ConcurrencyLimit = concurrency,
            })
            .UseRabbitMq()));

        Assert.Equal(option, refusal.ParamName);
        Assert.Contains("'ReadingBatchHandler'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_largest_grouping_a_broker_can_grant_is_accepted_and_a_time_limit_past_the_timers_is_refused()
    {
        Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging => messaging
            .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions
            {
                MessageLimit = 255,
                ConcurrencyLimit = 257,   // 65,535: the whole prefetch window
                TimeLimit = TimeSpan.FromMilliseconds(int.MaxValue),
            })
            .UseRabbitMq());

        var refusal = Assert.Throws<ArgumentOutOfRangeException>(() => Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging => messaging
            .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions { TimeLimit = TimeSpan.FromMilliseconds(int.MaxValue + 1L) })
            .UseRabbitMq()));
        Assert.Equal("TimeLimit", refusal.ParamName);
    }

    [Fact]
    public void The_batch_defaults_are_the_engines_and_the_grouping_is_fixed_at_registration()
    {
        var defaults = new GoldpathBatchOptions();
        Assert.Equal(10, defaults.MessageLimit);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.TimeLimit);
        Assert.Equal(1, defaults.ConcurrencyLimit);

        Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging =>
        {
            var options = new GoldpathBatchOptions { MessageLimit = 200 };
            messaging.AddBatchHandler<Reading, ReadingBatchHandler>(options);
            options.MessageLimit = 0;   // too late: what was registered is a copy

            var registered = Assert.Single(messaging.BatchHandlers);
            Assert.Equal(200, registered.Options.MessageLimit);
            Assert.Equal(GoldpathHandlerEndpoints.NameOf(typeof(ReadingBatchHandler)), registered.EndpointName);
            Assert.Equal("reading-batch", registered.EndpointName);
            messaging.UseRabbitMq();
        });
    }

    [Fact]
    public void A_batch_handler_registered_twice_is_refused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging => messaging
            .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions())
            .AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions())
            .UseRabbitMq()));

        Assert.Contains("'ReadingBatchHandler'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_handler_moved_to_a_second_endpoint_is_refused_and_the_same_name_twice_is_one()
    {
        Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging =>
        {
            messaging.AddHandler<Alarm, AlarmHandler>("fleet-db").AddHandler<Alarm, AlarmHandler>("fleet-db");
            Assert.Single(messaging.Handlers);
            Assert.Equal("fleet-db", messaging.EndpointOf(typeof(Alarm), typeof(AlarmHandler)));

            var moved = Assert.Throws<InvalidOperationException>(() => messaging.AddHandler<Alarm, AlarmHandler>("other-db"));
            Assert.Contains("'fleet-db'", moved.Message, StringComparison.Ordinal);
            Assert.Contains("'other-db'", moved.Message, StringComparison.Ordinal);
            messaging.UseRabbitMq();
        });

        Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging =>
        {
            // Registered under its derived name first, then named: that is a move too.
            messaging.AddHandler<Alarm, AlarmHandler>();
            Assert.Equal("alarm", messaging.EndpointOf(typeof(Alarm), typeof(AlarmHandler)));
            Assert.Throws<InvalidOperationException>(() => messaging.AddHandler<Alarm, AlarmHandler>("fleet-db"));
            messaging.AddHandler<Alarm, AlarmHandler>("alarm");   // naming the queue it already drains moves nothing
            messaging.UseRabbitMq();
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_endpoint_name_is_refused(string name)
    {
        Host.CreateApplicationBuilder().AddGoldpathMessaging(messaging =>
        {
            Assert.Throws<ArgumentException>(() => messaging.AddHandler<Alarm, AlarmHandler>(name));
            Assert.Throws<ArgumentException>(() => messaging.AddBatchHandler<Reading, ReadingBatchHandler>(new GoldpathBatchOptions(), name));
            Assert.Throws<ArgumentNullException>(() => messaging.AddBatchHandler<Reading, ReadingBatchHandler>(null!));
            messaging.UseRabbitMq();
        });
    }
}
