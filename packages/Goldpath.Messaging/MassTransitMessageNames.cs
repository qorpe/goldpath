using System.Collections.Concurrent;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using MassTransit;
using MassTransit.Configuration;
using MassTransit.Serialization;

namespace Goldpath;

/// <summary>
/// <see cref="GoldpathMessageNameAttribute"/> on the MassTransit 8.x engine (RFC goldpath-messaging-engines §4.1
/// row 15). 8.x reads a message's identity from its own attributes only, so the engine supplies it at the two
/// places it is used: the exchange a type publishes to and binds from (an entity-name formatter), and the
/// <c>messageType</c> URNs its envelope carries (a serializer that writes the wire name, and a deserializer that
/// lets a renamed handler's type match an envelope carrying the wire name). A type with no attribute passes
/// through both untouched, so an application that names nothing runs exactly as before.
/// </summary>
internal static class MassTransitMessageNames
{
    private static readonly ConcurrentDictionary<Type, string?> s_names = new();

    /// <summary>
    /// The wire name <paramref name="type"/> declares, or null. A name the attribute refuses is refused again here,
    /// naming the type, rather than surfacing from reflection on the first publish.
    /// </summary>
    public static string? Of(Type type)
        => s_names.GetOrAdd(type, static t =>
        {
            try
            {
                return t.GetCustomAttribute<GoldpathMessageNameAttribute>(inherit: false)?.Name;
            }
            catch (ArgumentException refused)
            {
                throw new InvalidOperationException($"The [GoldpathMessageName] on '{t.FullName}' cannot name it on the wire: {refused.Message}", refused);
            }
        });

    /// <summary>
    /// Applies the wire names to a bus: the exchange names of every named type, and the envelope URNs of
    /// <paramref name="handled"/> (the event types this bus handles, which a delivery must match).
    /// </summary>
    public static void Apply(IBusFactoryConfigurator configurator, IEnumerable<Type> handled)
    {
        // Every handled type's name is read now, so a refused one stops the start rather than the first delivery.
        var renamed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var type in handled.Distinct())
        {
            if (Of(type) is { } name && MessageUrn.ForTypeString(type) is var own && own != UrnOf(name))
            {
                if (!renamed.TryGetValue(UrnOf(name), out var owners))
                {
                    renamed[UrnOf(name)] = owners = [];
                }

                owners.Add(own);
            }
        }

        if (configurator.MessageTopology.EntityNameFormatter is not WireEntityNameFormatter)
        {
            configurator.MessageTopology.SetEntityNameFormatter(new WireEntityNameFormatter(configurator.MessageTopology.EntityNameFormatter));
        }

        var factory = new WireSerializerFactory(new SystemTextJsonMessageSerializerFactory(), renamed);
        configurator.AddSerializer(factory);
        configurator.AddDeserializer(factory, isDefault: true);
    }

    /// <summary>The URN at <paramref name="index"/>, or null where a foreign envelope put something other than a string.</summary>
    private static string? UrnAt(JsonArray messageTypes, int index)
        => messageTypes[index] is JsonValue value && value.TryGetValue<string>(out var urn) ? urn : null;

    /// <summary>The 8.x URN of a wire name.</summary>
    public static string UrnOf(string name) => "urn:message:" + name;

    /// <summary>Names a type's exchange by its wire name when it declares one, and as 8.x does otherwise.</summary>
    internal sealed class WireEntityNameFormatter(IEntityNameFormatter inner) : IEntityNameFormatter
    {
        public string FormatEntityName<T>() => Of(typeof(T)) ?? inner.FormatEntityName<T>();
    }

    /// <summary>The 8.x JSON serializer, with the envelope's <c>messageType</c> URNs mapped between CLR and wire names.</summary>
    internal sealed class WireSerializerFactory(ISerializerFactory inner, IReadOnlyDictionary<string, List<string>> renamed) : ISerializerFactory
    {
        public ContentType ContentType => inner.ContentType;

        public IMessageSerializer CreateSerializer() => new WireSerializer(inner.CreateSerializer());

        public IMessageDeserializer CreateDeserializer()
            => renamed.Count == 0 ? inner.CreateDeserializer() : new WireDeserializer(inner.CreateDeserializer(), renamed);
    }

    /// <summary>Writes a named type's URN, and its named base classes' URNs, as their wire names.</summary>
    internal sealed class WireSerializer(IMessageSerializer inner) : IMessageSerializer
    {
        private static readonly ConcurrentDictionary<Type, (string From, string To)[]> s_rewrites = new();

        public ContentType ContentType => inner.ContentType;

        public MessageBody GetMessageBody<T>(SendContext<T> context)
            where T : class
        {
            var body = inner.GetMessageBody(context);
            var rewrites = s_rewrites.GetOrAdd(typeof(T), static type =>
            {
                var found = new List<(string From, string To)>();
                for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
                {
                    if (Of(current) is { } name && MessageUrn.ForTypeString(current) is var own && own != UrnOf(name))
                    {
                        found.Add((own, UrnOf(name)));
                    }
                }

                return [.. found];
            });

            return rewrites.Length == 0 ? body : new RewrittenBody(body, envelope => Rename(envelope, rewrites));
        }

        private static void Rename(JsonArray messageTypes, (string From, string To)[] rewrites)
        {
            for (var i = 0; i < messageTypes.Count; i++)
            {
                if (UrnAt(messageTypes, i) is { } urn && Array.FindIndex(rewrites, r => r.From == urn) is var at and >= 0)
                {
                    messageTypes[i] = rewrites[at].To;
                }
            }
        }
    }

    /// <summary>Adds, beside a wire name an envelope carries, the URN of every handled type renamed from it.</summary>
    internal sealed class WireDeserializer(IMessageDeserializer inner, IReadOnlyDictionary<string, List<string>> renamed) : IMessageDeserializer
    {
        public ContentType ContentType => inner.ContentType;

        public void Probe(ProbeContext context) => inner.Probe(context);

        public ConsumeContext Deserialize(ReceiveContext receiveContext)
            => new BodyConsumeContext(receiveContext, Deserialize(receiveContext.Body, receiveContext.TransportHeaders, receiveContext.InputAddress));

        public SerializerContext Deserialize(MessageBody body, Headers headers, Uri? destinationAddress = null)
            => inner.Deserialize(new RewrittenBody(body, Add), headers, destinationAddress);

        public MessageBody GetMessageBody(string text) => inner.GetMessageBody(text);

        private void Add(JsonArray messageTypes)
        {
            var carried = Enumerable.Range(0, messageTypes.Count).Select(i => UrnAt(messageTypes, i)).OfType<string>().ToList();
            foreach (var urn in carried)
            {
                if (renamed.TryGetValue(urn, out var owners))
                {
                    foreach (var owner in owners.Where(o => !carried.Contains(o, StringComparer.Ordinal)))
                    {
                        messageTypes.Add(owner);
                    }
                }
            }
        }
    }

    /// <summary>
    /// An envelope body whose <c>messageType</c> array is changed by <paramref name="change"/>, read from the inner
    /// body once and only when asked for. A body that is not an 8.x JSON envelope passes through unchanged, for the
    /// inner serializer to accept or refuse as it would have.
    /// </summary>
    internal sealed class RewrittenBody(MessageBody inner, Action<JsonArray> change) : MessageBody
    {
        private byte[]? _bytes;

        public long? Length => GetBytes().Length;

        public Stream GetStream() => new MemoryStream(GetBytes(), writable: false);

        public byte[] GetBytes() => _bytes ??= Rewrite(inner.GetBytes(), change);

        public string GetString() => Encoding.UTF8.GetString(GetBytes());

        private static byte[] Rewrite(byte[] bytes, Action<JsonArray> change)
        {
            JsonNode? envelope;
            try
            {
                envelope = JsonNode.Parse(bytes);
            }
            catch (System.Text.Json.JsonException)
            {
                return bytes;
            }

            if (envelope is not JsonObject root || root["messageType"] is not JsonArray messageTypes)
            {
                return bytes;
            }

            change(messageTypes);
            return Encoding.UTF8.GetBytes(root.ToJsonString());
        }
    }
}
