using System.Security.Cryptography;
using System.Text;

namespace Goldpath;

/// <summary>
/// The inbox's consumer key, as the 8.x tables hold it (RFC goldpath-messaging-engines §4.4): <c>InboxState</c> is
/// keyed by (message id, consumer), and in a mixed fleet the consumer is the same whichever engine handled the
/// message, or a delivery handled under one engine runs its handler again under the other (S4). The default engine
/// keys a consumer by a hash of <c>"{consumer}:{message}:{queue}"</c>, where the consumer is its adapter consumer
/// <c>GoldpathIntegrationEventConsumer&lt;TEvent, THandler&gt;</c> — one per handler, so one row per handler — and
/// both type names are written the way 8.x writes a short type name. This class computes that key from the seam's
/// own registration: the event, the handler and the endpoint.
/// </summary>
internal static class GoldpathInboxKey
{
    // The default engine's adapter consumer. Its namespace and name are part of the inbox key every deployed
    // fleet has already written, so moving or renaming that type is a change to the shared schema, not a refactor.
    private const string DefaultEngineConsumer = "Goldpath.GoldpathIntegrationEventConsumer";

    /// <summary>The consumer id under which a handler's delivery of an event on an endpoint is recorded.</summary>
    public static Guid ConsumerIdOf(Type eventType, Type handlerType, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(handlerType);
        ArgumentException.ThrowIfNullOrEmpty(endpoint);
        var consumer = $"{DefaultEngineConsumer}<{ShortNameOf(eventType, "Goldpath")},{ShortNameOf(handlerType, "Goldpath")}>";
        return HashGuid($"{consumer}:{ShortNameOf(eventType)}:{endpoint}");
    }

    /// <summary>
    /// A type's short name as 8.x writes it: <c>Namespace.Type</c>, a nested type after <c>+</c>, generic arguments
    /// in <c>&lt;,&gt;</c>, and a namespace left out when it is the enclosing name's own (<paramref name="scope"/>).
    /// </summary>
    internal static string ShortNameOf(Type type, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsGenericTypeDefinition)
        {
            throw new ArgumentException($"'{type.Name}' is an open generic type; a consumer is keyed by a closed one.", nameof(type));
        }

        var name = new StringBuilder();
        Append(name, type, scope);
        return name.ToString();
    }

    private static void Append(StringBuilder name, Type type, string? scope)
    {
        if (type.IsGenericParameter)
        {
            return;
        }

        if (type.Namespace is { } ns && !ns.Equals(scope, StringComparison.Ordinal))
        {
            name.Append(ns).Append('.');
        }

        if (type.IsNested && type.DeclaringType is { } declaring)
        {
            Append(name, declaring, type.Namespace);
            name.Append('+');
        }

        if (!type.IsGenericType)
        {
            name.Append(type.Name);
            return;
        }

        var definition = type.GetGenericTypeDefinition().Name;
        var tick = definition.IndexOf('`', StringComparison.Ordinal);
        name.Append(tick > 0 ? definition[..tick] : definition).Append('<');
        var arguments = type.GenericTypeArguments;
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
            {
                name.Append(',');
            }

            Append(name, arguments[i], type.Namespace);
        }

        name.Append('>');
    }

    // MD5 as the default engine hashes it, and SHA-256's first sixteen bytes where it does: on a FIPS-only machine,
    // or when MT_FIPS_ENABLE is true. The two engines of one fleet must hash alike, so the switch is read the same way.
    private static Guid HashGuid(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        return IsFipsMode
            ? new Guid(SHA256.HashData(bytes).AsSpan(0, 16))
            : new Guid(MD5.HashData(bytes));
    }

    private static bool IsFipsMode
        => CryptoConfig.AllowOnlyFipsAlgorithms
            || (bool.TryParse(Environment.GetEnvironmentVariable("MT_FIPS_ENABLE"), out var enabled) && enabled);
}
