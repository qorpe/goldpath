using System.Collections.Concurrent;
using MassTransit;
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

    public TaskCompletionSource PoisonFaulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

/// <summary>The engine's own fault message — proves the poison left through the pipeline's door.</summary>
public sealed class PoisonFaultConsumer(Observed observed) : IConsumer<Fault<Poison>>
{
    public Task Consume(ConsumeContext<Fault<Poison>> context)
    {
        observed.PoisonFaulted.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>The app's own context with the outbox/inbox tables, as the template maps them.</summary>
public sealed class ConformanceDb(DbContextOptions<ConformanceDb> options) : DbContext(options)
{
    public DbSet<Row> Rows => Set<Row>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
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

    public async Task InitializeAsync() => await Task.WhenAll(Postgres.StartAsync(), Rabbit.StartAsync());

    public async Task DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await Rabbit.DisposeAsync();
    }

    /// <summary>
    /// Builds a host through the floor exactly as a generated app would: data, the outbox on
    /// the app's context, handlers through the seam, the transport block. Not started — a
    /// scenario decides when (the crash scenario commits BEFORE the bus exists).
    /// </summary>
    public async Task<IHost> BuildHostAsync(Observed observed, Action<GoldpathMessagingOptions>? options = null, string? tenant = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton(observed);
        if (tenant is not null)
        {
            builder.Services.AddSingleton<ITenantContext>(new FixedTenant(tenant));
        }

        builder.AddGoldpathData<HostApplicationBuilder, ConformanceDb>(o => o.UseNpgsql(Postgres.GetConnectionString()));
        // Engine wiring, so it lives in the harness, never in a fact: StartAsync returns only
        // once the bus has started. Every fact publishes right after it, and on a fresh broker
        // a publish that beats its queue's binding is dropped without an error (#371).
        builder.Services.Configure<MassTransitHostOptions>(o => o.WaitUntilStarted = true);
        builder.AddGoldpathMessaging(bus =>
        {
            bus.AddGoldpathHandler<Ping, PingHandler>();
            bus.AddGoldpathHandler<Poison, PoisonHandler>();
            bus.AddConsumer<PoisonFaultConsumer>();
            bus.AddGoldpathOutbox<ConformanceDb>(outbox => outbox.UsePostgres());
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(Rabbit.GetConnectionString()));
                cfg.ConfigureGoldpathEndpoints(context);
            });
        }, o =>
        {
            o.Retry.ImmediateCount = 2;
            o.Retry.RedeliveryIntervals.Clear();   // the suite proves the immediate ladder; delayed redelivery needs the scheduler plugin
            options?.Invoke(o);
        });

        var host = builder.Build();
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
    public static async Task PublishCommittedAsync<TEvent>(this IHost host, TEvent integrationEvent, string note = "row")
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

    /// <summary>
    /// The same committed path with the ENGINE's publish endpoint, for the facts that need
    /// to shape the message itself (a fixed message id, a wider or narrower body). The
    /// outbox still carries it — a publish from a root scope would sit in the bus outbox
    /// with nobody to flush it, which is how the first run of this suite delivered nothing.
    /// </summary>
    public static async Task PublishCommittedRawAsync(this IHost host, Func<IPublishEndpoint, Task> publish)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConformanceDb>();
        var endpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        db.Rows.Add(new Row { Id = Guid.NewGuid(), Note = "raw" });
        await publish(endpoint);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
