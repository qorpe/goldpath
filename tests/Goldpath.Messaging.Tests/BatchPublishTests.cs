using System.Collections.Concurrent;
using System.Reflection;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// #285: the publish seam's internal batch member. With no outbox the default engine keeps a BOUNDED number of
/// publishes outstanding instead of awaiting one round trip per event; with an outbox, or on a registration that did
/// not come through the engine, it publishes one at a time, because the scoped endpoint then writes into one
/// <c>DbContext</c>. A publisher without the member is published through one event at a time.
/// </summary>
public class BatchPublishTests
{
    public sealed record Reading(int Number) : IIntegrationEvent;

    [Fact]
    public async Task With_no_outbox_the_default_engine_keeps_up_to_the_bound_outstanding_and_never_more()
    {
        var endpoint = Endpoint.Create();
        var publisher = new MassTransitIntegrationEventPublisher(endpoint, new MassTransitPublishMode(ConcurrentOnTheWire: true));

        var batch = publisher.PublishBatchAsync([.. Enumerable.Range(0, 200).Select(i => new Reading(i))], CancellationToken.None);
        await Endpoint.Spy(endpoint).WaitOutstandingAsync(GoldpathBatchPublishing.MaxConcurrency);
        Assert.False(batch.IsCompleted);
        Endpoint.Spy(endpoint).ReleaseAll();
        await batch;

        Assert.Equal(GoldpathBatchPublishing.MaxConcurrency, Endpoint.Spy(endpoint).Peak);
        Assert.Equal(Enumerable.Range(0, 200), Endpoint.Spy(endpoint).Published.Select(r => r.Number).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task With_an_outbox_or_without_the_engines_mode_it_publishes_one_at_a_time_in_order(bool? concurrent)
    {
        var endpoint = Endpoint.Create();
        Endpoint.Spy(endpoint).Immediate = true;
        var publisher = new MassTransitIntegrationEventPublisher(
            endpoint, concurrent is { } c ? new MassTransitPublishMode(c) : null);

        await publisher.PublishBatchAsync([.. Enumerable.Range(0, 50).Select(i => new Reading(i))], CancellationToken.None);

        Assert.Equal(1, Endpoint.Spy(endpoint).Peak);
        Assert.Equal(Enumerable.Range(0, 50), Endpoint.Spy(endpoint).Published.Select(r => r.Number));
    }

    [Fact]
    public async Task A_publisher_without_the_batch_member_is_published_through_one_event_at_a_time()
    {
        var single = new SinglePublisher();

        await ((IIntegrationEventPublisher)single).PublishBatchAsync([new Reading(1), new Reading(2), new Reading(3)], CancellationToken.None);

        Assert.Equal([1, 2, 3], single.Published.Select(r => ((Reading)r).Number));
        Assert.Equal(1, single.Peak);
    }

    [Fact]
    public async Task An_empty_batch_publishes_nothing_and_a_null_one_is_refused()
    {
        var single = new SinglePublisher();
        await ((IIntegrationEventPublisher)single).PublishBatchAsync(Array.Empty<Reading>(), CancellationToken.None);
        Assert.Empty(single.Published);
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IIntegrationEventPublisher)single).PublishBatchAsync<Reading>(null!, CancellationToken.None));
    }

    /// <summary>
    /// A failure is rethrown only once every publish already started has settled, so nothing is left running against
    /// a scope the caller is about to dispose; and a publish that throws before its first await does not abandon the rest.
    /// </summary>
    [Fact]
    public async Task A_failure_is_rethrown_after_every_started_publish_has_settled()
    {
        var settled = 0;
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => GoldpathBatchPublishing.BoundedAsync(
            Enumerable.Range(0, 10).ToList(),
            async (i, _) =>
            {
                if (i == 3)
                {
                    throw new InvalidOperationException("refused 3");   // synchronous, before any await
                }

                await Task.Delay(50);
                Interlocked.Increment(ref settled);
            },
            CancellationToken.None));

        Assert.Equal("refused 3", refusal.Message);
        Assert.Equal(9, Volatile.Read(ref settled));
    }

    [Fact]
    public async Task A_cancelled_batch_starts_no_further_window()
    {
        using var cancel = new CancellationTokenSource();
        var started = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GoldpathBatchPublishing.BoundedAsync(
            Enumerable.Range(0, GoldpathBatchPublishing.MaxConcurrency * 3).ToList(),
            (_, _) =>
            {
                if (Interlocked.Increment(ref started) == GoldpathBatchPublishing.MaxConcurrency)
                {
                    cancel.Cancel();
                }

                return Task.CompletedTask;
            },
            cancel.Token));

        Assert.Equal(GoldpathBatchPublishing.MaxConcurrency, Volatile.Read(ref started));
    }

    /// <summary>
    /// A cancellation that arrives while the LAST window is publishing does not fail a batch whose every publish has
    /// completed: the caller would otherwise treat a fully published batch as failed and publish it again.
    /// </summary>
    [Fact]
    public async Task A_cancellation_during_the_last_window_does_not_fail_a_completed_batch()
    {
        using var cancel = new CancellationTokenSource();
        var items = Enumerable.Range(0, GoldpathBatchPublishing.MaxConcurrency * 2).ToList();   // an exact multiple: no window is left to start
        var published = 0;

        await GoldpathBatchPublishing.BoundedAsync(
            items,
            (i, _) =>
            {
                Interlocked.Increment(ref published);
                if (i == items[^1])
                {
                    cancel.Cancel();   // during the last window, after which nothing remains to start
                }

                return Task.CompletedTask;
            },
            cancel.Token);

        Assert.Equal(items.Count, Volatile.Read(ref published));
    }

    /// <summary>WIRING: the engine tells the publisher whether an outbox was composed.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task The_default_engine_composes_the_mode_from_the_outbox(bool outbox, bool concurrent)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["ConnectionStrings:messaging"] = "amqp://guest:guest@unused.invalid:5672";   // composed, never connected
        builder.Services.AddDbContext<OutboxDb>(o => o.UseSqlite("DataSource=:memory:"));
        builder.AddGoldpathMessaging(messaging =>
        {
            messaging.UseRabbitMq();
            if (outbox)
            {
                messaging.AddOutbox<OutboxDb>();
            }
        });

        await using var provider = builder.Services.BuildServiceProvider();
        Assert.Equal(concurrent, provider.GetRequiredService<MassTransitPublishMode>().ConcurrentOnTheWire);
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
        if (outbox)
        {
            // The outbox's size guard wraps the engine's publisher and carries no batch member: a batch through it is
            // published one event at a time, which is the outbox path's order anyway.
            Assert.IsNotAssignableFrom<IGoldpathBatchPublisher>(Assert.IsType<GoldpathSizeGuardPublisher>(publisher));
        }
        else
        {
            Assert.IsType<MassTransitIntegrationEventPublisher>(publisher);
        }
    }

    public sealed class OutboxDb(DbContextOptions<OutboxDb> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathOutboxEntities();
    }

    private sealed class SinglePublisher : IIntegrationEventPublisher
    {
        private int _outstanding;

        public ConcurrentQueue<object> Published { get; } = new();

        public int Peak { get; private set; }

        public async Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken = default)
            where TEvent : class, IIntegrationEvent
        {
            Peak = Math.Max(Peak, Interlocked.Increment(ref _outstanding));
            await Task.Yield();
            Published.Enqueue(integrationEvent);
            Interlocked.Decrement(ref _outstanding);
        }
    }

    /// <summary>
    /// An <see cref="IPublishEndpoint"/> whose <c>Publish&lt;T&gt;(T, CancellationToken)</c> is held until released,
    /// so a test can count how many are outstanding at once. Every other member is unused here.
    /// </summary>
    public class Endpoint : DispatchProxy
    {
        private readonly ConcurrentQueue<Reading> _published = new();
        private readonly List<TaskCompletionSource> _held = [];
        private readonly object _gate = new();
        private int _outstanding;
        private int _peak;

        public bool Immediate { get; set; }

        public int Peak => Volatile.Read(ref _peak);

        public IEnumerable<Reading> Published => _published;

        public static IPublishEndpoint Create() => Create<IPublishEndpoint, Endpoint>();

        public static Endpoint Spy(IPublishEndpoint endpoint) => (Endpoint)(object)endpoint;

        public async Task WaitOutstandingAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref _outstanding) < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            // Give an unbounded implementation the chance to exceed the bound before the test reads it.
            await Task.Delay(100);
        }

        public void ReleaseAll()
        {
            Immediate = true;
            lock (_gate)
            {
                foreach (var held in _held)
                {
                    held.TrySetResult();
                }

                _held.Clear();
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not { Name: "Publish", IsGenericMethod: true } || args is not [Reading reading, CancellationToken])
            {
                throw new NotSupportedException(targetMethod?.ToString());
            }

            return PublishAsync(reading);
        }

        private async Task PublishAsync(Reading reading)
        {
            var now = Interlocked.Increment(ref _outstanding);
            InterlockedMax(ref _peak, now);
            if (!Immediate)
            {
                var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    if (Immediate)
                    {
                        held.TrySetResult();
                    }
                    else
                    {
                        _held.Add(held);
                    }
                }

                await held.Task;
            }
            else
            {
                await Task.Yield();
            }

            _published.Enqueue(reading);
            Interlocked.Decrement(ref _outstanding);
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }
}
