using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Goldpath;

/// <summary>
/// A host for testing a message path in Goldpath's vocabulary (RFC goldpath-messaging-engines §4.3): the
/// application's registrations, the engine the package carries, and a publish that can shape the
/// message the way an upstream service would — no engine type in the test. It runs on the in-memory
/// transport unless the messaging block selects one (<see cref="GoldpathMessagingBuilder.UseRabbitMq"/>),
/// and <see cref="StartAsync(CancellationToken)"/> returns once the bus has started, so a publish right
/// after it is not lost to a binding that does not exist yet.
/// </summary>
public sealed class GoldpathMessagingTestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private GoldpathMessagingTestHost(IHost host) => _host = host;

    /// <summary>The host's services: the application's own, the publisher, the context the outbox rides on.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Builds the host without starting it: a test that commits before the bus exists starts it later.
    /// <paramref name="configureHost"/> runs first (services, data, configuration such as
    /// <c>ConnectionStrings:messaging</c>), then <paramref name="configureMessaging"/>.
    /// </summary>
    public static GoldpathMessagingTestHost Create(
        Action<GoldpathMessagingBuilder> configureMessaging,
        Action<HostApplicationBuilder>? configureHost = null)
    {
        ArgumentNullException.ThrowIfNull(configureMessaging);
        var builder = Host.CreateApplicationBuilder();
        configureHost?.Invoke(builder);

        // A test publishes right after the start; on a fresh broker a publish that beats its queue's
        // binding is dropped without an error, so the test host waits for the bus (the product default
        // is the owner's decision, open-threads T42).
        builder.Services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
        builder.AddGoldpathMessaging(messaging =>
        {
            configureMessaging(messaging);
            if (messaging.Transport == GoldpathMessagingTransport.None)
            {
                messaging.UseInMemory();
            }
        });
        return new GoldpathMessagingTestHost(builder.Build());
    }

    /// <summary>Builds the host as <see cref="Create"/> does and starts it.</summary>
    public static async Task<GoldpathMessagingTestHost> StartAsync(
        Action<GoldpathMessagingBuilder> configureMessaging,
        Action<HostApplicationBuilder>? configureHost = null)
    {
        var host = Create(configureMessaging, configureHost);
        await host.StartAsync(CancellationToken.None);
        return host;
    }

    /// <summary>Starts the host; returns once the bus is started.</summary>
    public Task StartAsync() => StartAsync(CancellationToken.None);

    /// <summary>Starts the host; returns once the bus is started.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => _host.StartAsync(cancellationToken);

    /// <summary>Stops the host gracefully: in-flight handlers finish, the rest stays on the queue.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default) => _host.StopAsync(cancellationToken);

    /// <summary>
    /// Publishes <paramref name="integrationEvent"/> straight to the broker, as an upstream service would,
    /// shaped by <paramref name="stamp"/> (a fixed message id, a tenant, a correlation, other headers).
    /// </summary>
    public Task PublishAsync<TEvent>(TEvent integrationEvent, Action<GoldpathOutgoingMessage>? stamp = null, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var outgoing = Stamp(stamp);
        return _host.Services.GetRequiredService<IBus>().Publish(integrationEvent, context => Apply(outgoing, context), cancellationToken);
    }

    /// <summary>
    /// Publishes a message under <typeparamref name="TEvent"/>'s contract whose body is
    /// <paramref name="body"/>'s properties — what a producer on a wider or an older version of the
    /// contract sends. Properties the contract lacks are carried; properties the body lacks are absent.
    /// The contract must be materialisable property by property: a class with a parameterless
    /// constructor and settable properties, not a positional record.
    /// </summary>
    public Task PublishAsAsync<TEvent>(object body, Action<GoldpathOutgoingMessage>? stamp = null, CancellationToken cancellationToken = default)
        where TEvent : class, IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(body);
        var outgoing = Stamp(stamp);
        return _host.Services.GetRequiredService<IBus>().Publish<TEvent>(body, context => Apply(outgoing, context), cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }

    private static GoldpathOutgoingMessage Stamp(Action<GoldpathOutgoingMessage>? stamp)
    {
        var outgoing = new GoldpathOutgoingMessage();
        stamp?.Invoke(outgoing);

        // The consume filter drops a tenant outside TenantId's alphabet, so a test stamped with one would
        // pass with a tenant that never arrived. An invalid header on purpose goes through Headers.
        if (outgoing.Tenant is { } tenant && !TenantId.TryCreate(tenant, out _))
        {
            throw new ArgumentException(
                $"'{tenant}' is not a valid tenant id: 1-{TenantId.MaxLength} chars from [a-z0-9-]. To send an invalid " +
                $"tenant header deliberately, set Headers[\"{GoldpathHeaders.TenantId}\"].",
                nameof(stamp));
        }

        return outgoing;
    }

    private static void Apply(GoldpathOutgoingMessage outgoing, PublishContext context)
    {
        if (outgoing.MessageId is { } messageId)
        {
            context.MessageId = messageId;
        }

        if (outgoing.Tenant is { } tenant)
        {
            context.Headers.Set(GoldpathHeaders.TenantId, tenant);
        }

        if (outgoing.CorrelationId is { } correlationId)
        {
            context.Headers.Set(GoldpathHeaders.CorrelationId, correlationId);
        }

        foreach (var (key, value) in outgoing.Headers)
        {
            context.Headers.Set(key, value);
        }
    }
}

/// <summary>How a test shapes a message it publishes through <see cref="GoldpathMessagingTestHost"/>.</summary>
public sealed class GoldpathOutgoingMessage
{
    /// <summary>The message id the inbox deduplicates on; the engine assigns one when null.</summary>
    public Guid? MessageId { get; set; }

    /// <summary>
    /// The tenant the message carries (<see cref="GoldpathHeaders.TenantId"/>); a value <see cref="TenantId"/>
    /// would refuse is refused at publish.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>The correlation the message carries (<see cref="GoldpathHeaders.CorrelationId"/>).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Any other header, by name.</summary>
    public IDictionary<string, object?> Headers { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
