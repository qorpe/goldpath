using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Goldpath.Messaging.Conformance;

/// <summary>
/// The events the suite speaks — plain integration events, nothing engine-specific. Classes
/// with settable properties rather than positional records ON PURPOSE: the versioning fact
/// publishes a wider and a narrower shape of the same message name, which needs a type an
/// engine can materialise property by property.
/// </summary>
public sealed class Ping : IIntegrationEvent
{
    public Guid Id { get; set; }

    public string Payload { get; set; } = "";
}

/// <summary>A handler that always throws — the poison scenario.</summary>
public sealed class Poison : IIntegrationEvent
{
    public Guid Id { get; set; }
}

/// <summary>What the handlers observed, for the assertions.</summary>
public sealed class Observed
{
    public ConcurrentBag<(Ping Event, IntegrationEventContext Context)> Pings { get; } = [];

    public ConcurrentBag<int> PoisonAttempts { get; } = [];

    /// <summary>Ping ids the second handler on a shared endpoint saw — both handlers of one event run for each delivery.</summary>
    public ConcurrentBag<Guid> AlsoPinged { get; } = [];

    /// <summary>The activity current inside each Ping handler: where the trace fact (S2.16) starts its walk.</summary>
    public ConcurrentBag<System.Diagnostics.Activity> Spans { get; } = [];

    /// <summary>While set and not completed, every Ping handler waits here — the drain scenario holds its window full.</summary>
    public Task? HandlerGate { get; set; }

    /// <summary>How long a Ping handler holds the message after the gate — the drain scenario slows it down.</summary>
    public TimeSpan HandlerDelay { get; set; }

    private int _started;

    /// <summary>Ping handlers entered so far, finished or not: the engine's in-flight window, while the gate holds.</summary>
    public int Started => Volatile.Read(ref _started);

    public void MarkStarted() => Interlocked.Increment(ref _started);

    public int PingCount => Pings.Count;

    /// <summary>
    /// Waits until no further handler has started for <paramref name="quiet"/>, and returns how
    /// many have — with the gate closed, that is the engine's window, whatever its size.
    /// </summary>
    public async Task<int> WaitForStartedToSettleAsync(TimeSpan quiet, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = -1;
        var since = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            var now = Started;
            if (now != last)
            {
                last = now;
                since = DateTime.UtcNow;
            }
            else if (now > 0 && DateTime.UtcNow - since >= quiet)
            {
                return now;
            }

            await Task.Delay(50);
        }

        return Started;
    }

    public async Task WaitForPingsAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (PingCount < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}

/// <summary>The seam-side handler under test: names no engine type.</summary>
public sealed class PingHandler(Observed observed) : IIntegrationEventHandler<Ping>
{
    public async Task HandleAsync(Ping integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.MarkStarted();
        if (observed.HandlerGate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        if (observed.HandlerDelay > TimeSpan.Zero)
        {
            await Task.Delay(observed.HandlerDelay, cancellationToken);
        }

        if (System.Diagnostics.Activity.Current is { } span)
        {
            observed.Spans.Add(span);
        }

        observed.Pings.Add((integrationEvent, context));
    }
}

/// <summary>Throws every time: the pipeline's retry ladder then the error queue.</summary>
public sealed class PoisonHandler(Observed observed) : IIntegrationEventHandler<Poison>
{
    public Task HandleAsync(Poison integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.PoisonAttempts.Add(context.RetryAttempt);
        throw new InvalidOperationException("poison");
    }
}

/// <summary>The batch facts' event: one per delivery, grouped by the engine before the handler sees it.</summary>
public sealed class Tick : IIntegrationEvent
{
    public int Number { get; set; }
}

/// <summary>What the batch handler observed: each batch's size and arrival, and how many ran at once.</summary>
public sealed class BatchesObserved
{
    private int _inFlight;
    private int _peak;

    public ConcurrentQueue<(int Size, DateTimeOffset At, IReadOnlyList<int> Numbers)> Batches { get; } = new();

    /// <summary>How long each batch holds the handler — the one-in-flight fact needs batches that overlap if allowed to.</summary>
    public TimeSpan Hold { get; set; }

    /// <summary>When set, every batch publishes one Ping through the seam and saves nothing itself.</summary>
    public bool PublishFromBatch { get; set; }

    /// <summary>The most batches ever inside the handler at once.</summary>
    public int Peak => Volatile.Read(ref _peak);

    public int Delivered => Batches.Sum(b => b.Size);

    public int Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
        {
        }

        return now;
    }

    public void Leave() => Interlocked.Decrement(ref _inFlight);

    public async Task WaitForDeliveredAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Delivered < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}

/// <summary>The seam's batch handler under test: names no engine type.</summary>
public sealed class TickBatchHandler(BatchesObserved observed, IIntegrationEventPublisher publisher) : IIntegrationEventBatchHandler<Tick>
{
    public async Task HandleAsync(IReadOnlyList<IntegrationEvent<Tick>> batch, CancellationToken cancellationToken = default)
    {
        observed.Enter();
        try
        {
            if (observed.Hold > TimeSpan.Zero)
            {
                await Task.Delay(observed.Hold, cancellationToken);
            }

            if (observed.PublishFromBatch)
            {
                await publisher.PublishAsync(new Ping { Id = Guid.NewGuid(), Payload = "from-batch" }, cancellationToken);
            }

            observed.Batches.Enqueue((batch.Count, DateTimeOffset.UtcNow, [.. batch.Select(e => e.Message.Number)]));
        }
        finally
        {
            observed.Leave();
        }
    }
}

/// <summary>The concurrency and ordering facts' event (S2.14, S2.15): one per delivery, numbered by its publisher.</summary>
public sealed class Work : IIntegrationEvent
{
    public int Number { get; set; }
}

/// <summary>What <see cref="WorkHandler"/> observed: the order deliveries were handled in, and how many ran at once.</summary>
public sealed class WorkObserved
{
    private int _inFlight;
    private int _peak;

    public ConcurrentQueue<int> Handled { get; } = new();

    /// <summary>How long each delivery holds the handler, so deliveries an engine runs together overlap.</summary>
    public TimeSpan Hold { get; set; }

    /// <summary>The most deliveries ever inside the handler at once.</summary>
    public int Peak => Volatile.Read(ref _peak);

    public void Enter()
    {
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
        {
        }
    }

    public void Leave() => Interlocked.Decrement(ref _inFlight);

    public async Task WaitForHandledAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Handled.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}

/// <summary>The seam-side handler of <see cref="Work"/>: names no engine type.</summary>
public sealed class WorkHandler(WorkObserved observed) : IIntegrationEventHandler<Work>
{
    public async Task HandleAsync(Work integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.Enter();
        try
        {
            // A delivery the engine runs beside another one waits a little longer the lower its number, so an engine
            // running several at once finishes them out of order rather than by luck in order.
            await Task.Delay(observed.Hold + TimeSpan.FromMilliseconds(5 * (10 - (integrationEvent.Number % 10))), cancellationToken);
            observed.Handled.Enqueue(integrationEvent.Number);
        }
        finally
        {
            observed.Leave();
        }
    }
}

/// <summary>The poison of a limited endpoint (S2.18): its own queue, so its error queue counts only its own faults.</summary>
public sealed class LimitedPoison : IIntegrationEvent
{
    public Guid Id { get; set; }
}

/// <summary>Throws every time, on an endpoint run at a concurrency limit.</summary>
public sealed class LimitedPoisonHandler(Observed observed) : IIntegrationEventHandler<LimitedPoison>
{
    public Task HandleAsync(LimitedPoison integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.PoisonAttempts.Add(context.RetryAttempt);
        throw new InvalidOperationException("poison");
    }
}

/// <summary>What the renamed-type fact's handlers observed (S2.17): each delivery's id, by the type that took it.</summary>
public sealed class RenameObserved
{
    public ConcurrentBag<Guid> AsOld { get; } = [];

    public ConcurrentBag<Guid> AsNew { get; } = [];

    public async Task WaitForAsync(int each, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((AsOld.Count < each || AsNew.Count < each) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
    }
}

public sealed class OrderPlacedHandler(RenameObserved observed) : IIntegrationEventHandler<Legacy.OrderPlaced>
{
    public Task HandleAsync(Legacy.OrderPlaced integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.AsOld.Add(integrationEvent.Id);
        return Task.CompletedTask;
    }
}

public sealed class OrderSubmittedHandler(RenameObserved observed) : IIntegrationEventHandler<OrderSubmitted>
{
    public Task HandleAsync(OrderSubmitted integrationEvent, IntegrationEventContext context, CancellationToken cancellationToken = default)
    {
        observed.AsNew.Add(integrationEvent.Id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The renamed type (S2.17): what <see cref="Legacy.OrderPlaced"/> became, in another namespace and under another
/// name, keeping the old one's wire identity.
/// </summary>
[GoldpathMessageName("Goldpath.Messaging.Conformance.Legacy:OrderPlaced")]
public sealed class OrderSubmitted : IIntegrationEvent
{
    public Guid Id { get; set; }
}

/// <summary>The app's own context with the Goldpath outbox schema (RFC goldpath-messaging-engines §4.2).</summary>
public sealed class ConformanceDb(DbContextOptions<ConformanceDb> options) : DbContext(options)
{
    public DbSet<Row> Rows => Set<Row>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddGoldpathOutboxEntities();
    }
}

public sealed class Row
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// One RabbitMQ + one PostgreSQL per test class; hosts are built per scenario on the SAME
/// containers so a scenario can stop a host, pause the broker, or start a second process.
/// </summary>
public sealed class ConformanceContainers : IAsyncLifetime
{
    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder("postgres:17-alpine").Build();

    // The management plugin is part of the harness: the error queue is a FACT the suite
    // reads (runbook: faults land in <queue>_error), so the broker must answer for it.
    public RabbitMqContainer Rabbit { get; } = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithUsername("guest").WithPassword("guest")
        .WithPortBinding(15672, true)
        .Build();

    /// <summary>Messages sitting in a queue, by the management API — the operator's own view.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Rabbit.Hostname}:{Rabbit.GetMappedPublicPort(15672)}/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("guest:guest")));
        using var response = await http.GetAsync($"api/queues/%2F/{queue}");
        if (!response.IsSuccessStatusCode)
        {
            return 0;   // the queue does not exist yet
        }

        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("messages", out var messages) ? messages.GetInt32() : 0;
    }

    /// <summary>Whether the broker holds an exchange named <paramref name="exchange"/>, by the management API.</summary>
    public async Task<bool> ExchangeExistsAsync(string exchange)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{Rabbit.Hostname}:{Rabbit.GetMappedPublicPort(15672)}/") };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("guest:guest")));
        using var response = await http.GetAsync($"api/exchanges/%2F/{Uri.EscapeDataString(exchange)}");
        return response.IsSuccessStatusCode;
    }

    public async Task InitializeAsync() => await Task.WhenAll(Postgres.StartAsync(), Rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Rabbit.DisposeAsync();
    }

    /// <summary>
    /// Builds a host through the floor exactly as a generated app would, in Goldpath's vocabulary only
    /// (RFC goldpath-messaging-engines §4.3): data, the outbox on the app's context, handlers through the
    /// seam, RabbitMQ at the connection string the AppHost would give. Not started — a scenario decides
    /// when (the crash scenario commits BEFORE the bus exists). The test host returns from its start
    /// only once the bus has started (#371), so no fact has to know how an engine says that. Every engine
    /// the suite references is in this process, so <paramref name="engine"/> names the one this host runs on;
    /// <paramref name="compose"/> adds to the composition after the defaults, and <paramref name="services"/> to the host's services.
    /// </summary>
    public async Task<GoldpathMessagingTestHost> BuildHostAsync(
        string engine, Observed observed, Action<GoldpathMessagingOptions>? options = null, string? tenant = null,
        (BatchesObserved Observed, GoldpathBatchOptions Grouping)? batches = null, Action<GoldpathMessagingBuilder>? compose = null,
        Action<IServiceCollection>? services = null)
    {
        var host = GoldpathMessagingTestHost.Create(
            messaging =>
            {
                messaging.AddHandler<Ping, PingHandler>();
                messaging.AddHandler<Poison, PoisonHandler>();
                if (batches is { } batch)
                {
                    messaging.AddBatchHandler<Tick, TickBatchHandler>(batch.Grouping);
                }

                messaging.AddOutbox<ConformanceDb>();
                messaging.UseRabbitMq();
                messaging.Options.Retry.ImmediateCount = 2;
                messaging.Options.Retry.RedeliveryIntervals.Clear();   // the suite proves the immediate ladder; delayed redelivery needs the scheduler plugin
                options?.Invoke(messaging.Options);
                compose?.Invoke(messaging);
            },
            builder =>
            {
                builder.Configuration["ConnectionStrings:messaging"] = Rabbit.GetConnectionString();
                builder.Services.AddSingleton(observed);
                if (batches is { } batch)
                {
                    builder.Services.AddSingleton(batch.Observed);
                }
                if (tenant is not null)
                {
                    builder.Services.AddSingleton<ITenantContext>(new FixedTenant(tenant));
                }

                builder.AddGoldpathData<HostApplicationBuilder, ConformanceDb>(o => o.UseNpgsql(Postgres.GetConnectionString()));
                services?.Invoke(builder.Services);
            },
            engine);

        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConformanceDb>().Database.EnsureCreatedAsync();
        return host;
    }

    private sealed class FixedTenant(string value) : ITenantContext
    {
        public TenantId? Current => TenantId.Create(value);
    }
}

public static class HostExtensions
{
    /// <summary>Publishes through the seam inside a committed transaction — the outbox path an app takes.</summary>
    public static async Task PublishCommittedAsync<TEvent>(this GoldpathMessagingTestHost host, TEvent integrationEvent, string note = "row")
        where TEvent : class, IIntegrationEvent
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Rows.Add(new Row { Id = Guid.NewGuid(), Note = note });
        await publisher.PublishAsync(integrationEvent);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
