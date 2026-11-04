namespace Goldpath;

/// <summary>
/// Fixes an integration event's identity on the wire (RFC goldpath-messaging-engines §4.1 row 15), so the CLR
/// type can be renamed or moved without moving its messages. Without it, the identity is derived from the type:
/// <c>Namespace:Type</c> names the event's exchange, and <c>urn:message:Namespace:Type</c> is the type the
/// envelope carries. With it, <see cref="Name"/> is both, on every engine, for publishing and for handling. It is
/// part of the wire a mixed fleet keeps (messaging-exit §7.1 S4): to rename <c>OrderPlaced</c> to
/// <c>OrderSubmitted</c>, put <c>[GoldpathMessageName("Contoso.Orders:OrderPlaced")]</c> on the new type, and
/// the producers and consumers still on the old name keep exchanging messages with it.
/// </summary>
/// <remarks>Not inherited: a type derived from a named event is a different message, with its own identity.</remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class GoldpathMessageNameAttribute : Attribute
{
    /// <summary>The most UTF-8 bytes an exchange name may hold (AMQP's short string).</summary>
    private const int MaxBytes = 255;

    /// <summary>Fixes the wire identity to <paramref name="name"/>.</summary>
    /// <param name="name">
    /// The name in the 8.x <c>Namespace:Type</c> form, without the <c>urn:message:</c> prefix: at most 255 UTF-8
    /// bytes, with no whitespace, control character, <c>/</c>, <c>?</c> or <c>#</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> cannot name an exchange and a message URN.</exception>
    public GoldpathMessageNameAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith("urn:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{name}' carries a URN prefix; name the message as Namespace:Type, and the engine adds urn:message:.", nameof(name));
        }

        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '?' or '#'))
        {
            throw new ArgumentException($"'{name}' holds whitespace, a control character, '/', '?' or '#', which an exchange name or a message URN cannot carry.", nameof(name));
        }

        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxBytes)
        {
            throw new ArgumentException($"A message name is at most {MaxBytes} UTF-8 bytes, the longest exchange name a broker accepts.", nameof(name));
        }

        if (!Uri.TryCreate("urn:message:" + name, UriKind.Absolute, out _))
        {
            throw new ArgumentException($"'urn:message:{name}' is not a URI.", nameof(name));
        }

        Name = name;
    }

    /// <summary>The wire name: the exchange's name, and the message URN after <c>urn:message:</c>.</summary>
    public string Name { get; }
}
