using System.Text;
using System.Text.RegularExpressions;

namespace Goldpath;

/// <summary>
/// What a failed send may say about itself (#532, threat T-NTF-09). A channel's exception text is the provider's own
/// words, and providers echo who they refused: an SMTP reply names the rejected address, an SMS gateway the number.
/// The recipient is masked on every admin surface, so the text is masked BEFORE it reaches <c>Detail</c>, the jobs
/// console's repair reason or the log, and it is masked fail-closed: not only the row's own recipient, in any letter
/// case, but anything shaped like an address, a phone number or a URL's credentials, because a provider may name a
/// recipient the row does not hold (a forwarding address, a reformatted number). Then it is flattened to one line and
/// bounded to <see cref="MaxLength"/>, the <c>Detail</c> column's length. What it returns is always well-formed UTF-16:
/// no cut falls between a surrogate pair, a kept hint is a whole character, and any lone surrogate left (a provider's
/// own malformed text) becomes U+FFFD, because a database that encodes strictly (Npgsql) refuses to save anything else.
/// </summary>
internal static partial class GoldpathNotificationFailureText
{
    /// <summary>The longest text kept: <c>Detail</c>'s column length (<c>NotificationModel</c>). Longer is cut and marked.</summary>
    public const int MaxLength = 1024;

    /// <summary>What a cut text ends with.</summary>
    public const string CutMarker = "…";

    /// <summary>The shortest local part masked wherever it appears on its own (<c>omer</c> in "user omer unknown").</summary>
    private const int MinLocalPartLength = 3;

    /// <summary>The longest provider text read at all; beyond it the text is cut at a word boundary before masking, so no half token is left.</summary>
    public const int MaxReadLength = 8 * MaxLength;

    /// <summary>What is kept when the text cannot be masked in time: nothing of the provider's words.</summary>
    public const string Withheld = "the channel's failure text could not be masked and is withheld";

    /// <summary>The masked text of <paramref name="failure"/>'s message, for a notification to <paramref name="recipient"/>.</summary>
    public static string Of(Exception failure, string recipient)
        => Mask(failure.Message, recipient);

    /// <summary>
    /// Masks <paramref name="text"/>: URLs' credentials and queries, every address-shaped and phone-shaped token, then
    /// every remaining case-insensitive occurrence of <paramref name="recipient"/> and of its local part; control
    /// characters become spaces; the result is at most <see cref="MaxLength"/> characters.
    /// </summary>
    public static string Mask(string? text, string recipient)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        try
        {
            return MaskBounded(text, recipient);
        }
        catch (RegexMatchTimeoutException)
        {
            return Withheld;   // fail closed: an unmaskable text is not shown at all
        }
    }

    private static string MaskBounded(string text, string recipient)
    {
        if (text.Length > MaxReadLength)
        {
            var cut = text.LastIndexOfAny([' ', '\t', '\n', '\r'], MaxReadLength);
            text = string.Concat(text.AsSpan(0, cut > 0 ? cut : WholeCharacters(text, MaxReadLength)), " ", CutMarker);
        }

        // One well-formed line FIRST, the recipient too: a lone half the provider (or the store) turned into U+FFFD on
        // one side and not the other would otherwise keep the recipient from matching itself.
        text = OneLine(text);
        recipient = OneLine(recipient);
        var masked = Url().Replace(text, m => MaskUrl(m.Value));
        masked = Address().Replace(masked, m => MaskRecipient(m.Value.Replace("%40", "@", StringComparison.OrdinalIgnoreCase)));
        masked = Phone().Replace(masked, m => $"{First(m.Value)}***");
        if (recipient.Length > 0)
        {
            masked = masked.Replace(recipient, MaskRecipient(recipient), StringComparison.OrdinalIgnoreCase);
            var at = recipient.IndexOf('@');
            if (at >= MinLocalPartLength)
            {
                masked = masked.Replace(recipient[..at], $"{First(recipient)}***", StringComparison.OrdinalIgnoreCase);
            }
        }

        return Bounded(masked);
    }

    /// <summary>
    /// Masks a recipient for the admin surface: first character + domain hint survive
    /// (enough to triage "wrong customer?", never enough to copy an address off a screen).
    /// </summary>
    public static string MaskRecipient(string recipient)
    {
        if (recipient.Length == 0)
        {
            return "";
        }

        var at = recipient.IndexOf('@');
        if (at > 0 && at < recipient.Length - 1)
        {
            var domain = recipient[(at + 1)..];
            return $"{First(recipient)}***@{First(domain)}***";
        }

        return $"{First(recipient)}***";
    }

    /// <summary>The first whole character of a non-empty text: both halves of a surrogate pair, or U+FFFD for a lone one.</summary>
    private static string First(string text)
    {
        Rune.DecodeFromUtf16(text, out var first, out _);   // a lone surrogate decodes as Rune.ReplacementChar
        return first.ToString();
    }

    /// <summary>
    /// The longest prefix of at most <paramref name="length"/> characters that does not end on a high surrogate, whether
    /// it starts a pair the cut would split or stands alone. Both callers cut a text LONGER than
    /// <paramref name="length"/>, and <paramref name="length"/> is positive, so <c>text[length - 1]</c> exists.
    /// </summary>
    private static int WholeCharacters(string text, int length)
        => char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;

    private static string MaskUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "***";
        }

        // Never re-emit an '@': the address rule below would read the kept URL as an address.
        var kept = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            kept += "?***";
        }

        return uri.UserInfo.Length > 0 ? kept + " (credentials removed)" : kept;
    }

    private static string OneLine(string text)
    {
        var line = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                line.Append(c).Append(text[++i]);   // a whole pair is kept
            }
            else if (char.IsSurrogate(c))
            {
                line.Append('\uFFFD');   // a lone half is no character: a strict encoder refuses it
            }
            else
            {
                line.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c);
            }
        }

        return line.ToString();
    }

    private static string Bounded(string text)
        => text.Length <= MaxLength ? text : string.Concat(text.AsSpan(0, WholeCharacters(text, MaxLength - CutMarker.Length)), CutMarker);

    /// <summary>An absolute URL, up to the next whitespace or bracket.</summary>
    [GeneratedRegex(@"\b[a-zA-Z][a-zA-Z0-9+.\-]*://[^\s<>""'()\[\]{}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Url();

    /// <summary>An address: a local part, <c>@</c> or its percent-encoding <c>%40</c>, and a domain.</summary>
    [GeneratedRegex(@"[^\s<>""'(),;:\[\]{}@]+(?:@|%40)[^\s<>""'(),;:\[\]{}@]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Address();

    /// <summary>
    /// A phone-shaped run: seven or more digits, optionally led by <c>+</c>, with up to two separators between digits so
    /// a parenthesised area code (<c>+1 (555) 123-4567</c>: <c> (</c> and <c>) </c>) does not break the run.
    /// </summary>
    [GeneratedRegex(@"\+?\d(?:[ .\-/()]{0,2}\d){6,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Phone();
}
