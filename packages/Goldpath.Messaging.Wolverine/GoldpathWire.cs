using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using RabbitMQ.Client;

namespace Goldpath;

/// <summary>
/// The wire every adapter shares (RFC goldpath-messaging-engines §4.4): the MassTransit 8.x JSON envelope
/// and the 8.x RabbitMQ topology — one fanout exchange per message type named <c>Namespace:Type</c>, and per
/// endpoint a queue plus an exchange of the same name, with <c>&lt;queue&gt;_error</c> beside it. This adapter
/// writes that envelope itself and declares that topology itself, so what it puts on the broker is what the
/// default engine already reads, rather than whatever an engine's interoperability mode happens to emit.
/// </summary>
internal sealed class GoldpathWire : IAsyncDisposable
{
    /// <summary>The 8.x envelope's content type.</summary>
    public const string ContentType = "application/vnd.masstransit+json";

    /// <summary>
    /// The 8.x header that carries the publisher's span, as a W3C <c>traceparent</c>: the consumer's span continues
    /// that trace (RFC goldpath-messaging-engines §4.4; messaging-exit §7.1 S2.16).
    /// </summary>
    public const string ActivityIdHeader = "MT-Activity-Id";

    private static readonly JsonSerializerOptions s_body = new(JsonSerializerDefaults.Web);

    private readonly Uri _broker;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
    private IConnection? _connection;
    private IChannel? _channel;

    public GoldpathWire(Uri broker)
    {
        _broker = broker;

        // The addresses an envelope carries name the broker, never its credentials: the connection string's
        // user and password would otherwise travel in every message and sit in every outbox row. AMQP's own
        // port is left out, as 8.x addresses leave it out.
        Address = new UriBuilder(broker) { Scheme = "rabbitmq", UserName = "", Password = "", Port = broker.IsDefaultPort || broker.Port == 5672 ? -1 : broker.Port }.Uri;
    }

    /// <summary>The broker's address as envelopes name it: <c>rabbitmq://host[:port]/vhost</c>, with no credentials.</summary>
    public Uri Address { get; }

    /// <summary>
    /// An exchange's or queue's address under the broker, <c>rabbitmq://host[:port]/vhost/name</c>. Composed, not
    /// resolved as a relative URI: <c>Namespace:Type</c> would read as a scheme.
    /// </summary>
    public Uri AddressOf(string name) => new($"{Address.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{name}");

    /// <summary>The broker the composition names: the connection string when present, the engine's local default when absent.</summary>
    public static Uri BrokerOf(GoldpathMessagingComposition composition, IConfiguration configuration)
    {
        if (configuration.GetConnectionString(composition.RabbitMqConnectionName) is not { } connection)
        {
            return new Uri("amqp://guest:guest@localhost:5672/");
        }

        // Absent means the local default; present and blank is a mistake, refused in words (as the default engine does).
        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{composition.RabbitMqConnectionName} is present but empty. Set the broker's " +
                "address, or remove the entry to use the engine's local default.");
        }

        return new Uri(connection);
    }

    public Uri Broker => _broker;

    /// <summary>
    /// The 8.x name of a message type's exchange and its <c>urn:message:</c> identity: the
    /// <see cref="GoldpathMessageNameAttribute"/> the type declares (RFC goldpath-messaging-engines §4.1 row 15), or
    /// the name 8.x derives from the type.
    /// </summary>
    public static string ExchangeOf(Type messageType)
    {
        if (NameOf(messageType) is { } declared)
        {
            return declared;
        }

        if (messageType.IsGenericType)
        {
            throw new NotSupportedException($"'{messageType.Name}' is generic; a generic integration event has no 8.x exchange name this adapter derives.");
        }

        var name = messageType.IsNested ? $"{ExchangeOf(messageType.DeclaringType!).Split(':')[^1]}+{messageType.Name}" : messageType.Name;
        return string.IsNullOrEmpty(messageType.Namespace) ? name : $"{messageType.Namespace}:{name}";
    }

    /// <summary>
    /// The wire name <paramref name="messageType"/> declares, or null. A name the attribute refuses is refused again
    /// here, naming the type.
    /// </summary>
    public static string? NameOf(Type messageType)
    {
        try
        {
            return messageType.GetCustomAttribute<GoldpathMessageNameAttribute>(inherit: false)?.Name;
        }
        catch (ArgumentException refused)
        {
            throw new InvalidOperationException($"The [GoldpathMessageName] on '{messageType.FullName}' cannot name it on the wire: {refused.Message}", refused);
        }
    }

    /// <summary>
    /// Stamps the current span on <paramref name="headers"/> under <see cref="ActivityIdHeader"/>, unless the caller
    /// stamped one already; a span outside W3C format, or none, stamps nothing.
    /// </summary>
    public static void StampTrace(IDictionary<string, string> headers)
    {
        if (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } id } && !headers.ContainsKey(ActivityIdHeader))
        {
            headers[ActivityIdHeader] = id;
        }
    }

    /// <summary>The 8.x <c>messageType</c> URN of a message type.</summary>
    public static string UrnOf(Type messageType) => $"urn:message:{ExchangeOf(messageType)}";

    /// <summary>
    /// Builds the 8.x envelope for <paramref name="body"/> under <paramref name="contract"/>: what the outbox
    /// stores and what goes on the wire. The message serialises with web defaults (camelCase), as 8.x does.
    /// </summary>
    public byte[] Envelope(Type contract, object body, Guid messageId, IReadOnlyDictionary<string, string> headers, DateTimeOffset sentTime)
    {
        var exchange = ExchangeOf(contract);
        var envelope = new JsonObject
        {
            ["messageId"] = messageId.ToString(),
            ["conversationId"] = messageId.ToString(),
            ["sourceAddress"] = AddressOf("goldpath").ToString(),
            ["destinationAddress"] = AddressOf(exchange).ToString(),
            ["messageType"] = new JsonArray(UrnOf(contract)),
            ["message"] = JsonSerializer.SerializeToNode(body, body.GetType(), s_body),
            ["sentTime"] = sentTime.UtcDateTime.ToString("O"),
            ["headers"] = new JsonObject(headers.Select(h => KeyValuePair.Create(h.Key, (JsonNode?)JsonValue.Create(h.Value)))),
        };

        return Encoding.UTF8.GetBytes(envelope.ToJsonString());
    }

    /// <summary>
    /// Declares the endpoint topology: the queue, its same-named exchange bound to it, each message type's
    /// exchange bound to the endpoint's, and the <c>_error</c> exchange and queue.
    /// </summary>
    public async Task DeclareEndpointAsync(string endpoint, IEnumerable<Type> messageTypes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await ChannelAsync(cancellationToken);
            await DeclareQueueWithExchangeAsync(channel, endpoint, cancellationToken);
            await DeclareQueueWithExchangeAsync(channel, $"{endpoint}_error", cancellationToken);
            foreach (var messageType in messageTypes)
            {
                var exchange = await DeclareMessageExchangeAsync(channel, messageType, cancellationToken);
                await channel.ExchangeBindAsync(endpoint, exchange, "", cancellationToken: cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Publishes an 8.x envelope to the exchange named <paramref name="exchange"/> and waits for the broker's confirm.</summary>
    public async Task PublishAsync(string exchange, Guid messageId, byte[] envelope, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await ChannelAsync(cancellationToken);
            await DeclareExchangeAsync(channel, exchange, cancellationToken);
            var properties = new BasicProperties
            {
                ContentType = ContentType,
                MessageId = messageId.ToString(),
                DeliveryMode = DeliveryModes.Persistent,
            };
            await channel.BasicPublishAsync(exchange, "", mandatory: false, properties, envelope, cancellationToken);
        }
        catch when (_channel is { IsOpen: false })
        {
            // A broken channel is rebuilt on the next publish; the caller (the relay) retries the row.
            _channel = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    private async Task<string> DeclareMessageExchangeAsync(IChannel channel, Type messageType, CancellationToken cancellationToken)
    {
        var exchange = ExchangeOf(messageType);
        await DeclareExchangeAsync(channel, exchange, cancellationToken);
        return exchange;
    }

    private async Task DeclareExchangeAsync(IChannel channel, string exchange, CancellationToken cancellationToken)
    {
        // Recorded only once the broker has declared it: a declare cancelled on an open channel must be tried again.
        if (!_declared.Contains(exchange))
        {
            await channel.ExchangeDeclareAsync(exchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
            _declared.Add(exchange);
        }
    }

    /// <summary>The exchange an 8.x <c>urn:message:</c> identity is published to.</summary>
    public static string ExchangeOfUrn(string urn)
        => urn.StartsWith("urn:message:", StringComparison.Ordinal) ? urn["urn:message:".Length..] : throw new FormatException($"'{urn}' is not an 8.x message type URN.");

    private static async Task DeclareQueueWithExchangeAsync(IChannel channel, string name, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(name, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(name, name, "", cancellationToken: cancellationToken);
    }

    private async Task<IChannel> ChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        if (_connection is not { IsOpen: true })
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            _connection = await new ConnectionFactory { Uri = _broker }.CreateConnectionAsync("goldpath-wire", cancellationToken);
        }

        _declared.Clear();
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        return _channel;
    }
}
