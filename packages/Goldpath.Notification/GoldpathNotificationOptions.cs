using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Goldpath;

/// <summary>One rendered message on its way to a channel (the channel CONTRACT, D1).</summary>
public sealed record GoldpathNotificationMessage(
    Guid NotificationId,
    string Recipient,
    string? Subject,
    string Body,
    IReadOnlyList<GoldpathNotificationAttachmentContent> Attachments,
    string Template,
    string? Tenant,
    string? CorrelationId);

/// <summary>One attachment's content in the channel contract.</summary>
public sealed record GoldpathNotificationAttachmentContent(string Name, string ContentType, byte[] Content);

/// <summary>
/// The channel seam (notification RFC D1): enterprises plug their own gateways. SendAsync
/// returning normally means the channel ACCEPTED the message (accepted ≠ delivered);
/// throwing consumes an attempt.
/// </summary>
public interface IGoldpathNotificationChannel
{
    /// <summary>The channel name templates bind to (email, webhook, sms, ...).</summary>
    string Name { get; }

    /// <summary>Hands one message to the underlying transport.</summary>
    Task SendAsync(GoldpathNotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>One registered template: per channel, per culture, baked and hash-stamped (D4).</summary>
public sealed class GoldpathNotificationTemplate
{
    internal GoldpathNotificationTemplate(string key) => Key = key;

    /// <summary>The registration key requests name.</summary>
    public string Key { get; }

    /// <summary>Retention window for rendered bodies + attachments (GP1602 makes its absence visible).</summary>
    public TimeSpan? DeleteBodyAfter { get; internal set; }

    /// <summary>SHA-256 over the registered content — stamped into every evidence row.</summary>
    public string Hash { get; internal set; } = "";

    internal Dictionary<string, GoldpathChannelTemplate> Channels { get; } = new(StringComparer.Ordinal);

    /// <summary>Resolves the channel template or fails with a teaching message.</summary>
    public GoldpathChannelTemplate ChannelTemplate(string channel)
        => Channels.TryGetValue(channel, out var template)
            ? template
            : throw new InvalidOperationException(
                $"Template '{Key}' has no '{channel}' channel — registered: {string.Join(", ", Channels.Keys)}.");
}

/// <summary>One template's texts for one channel, per culture with fallback.</summary>
public sealed class GoldpathChannelTemplate
{
    internal Dictionary<string, (string? Subject, string Body)> Cultures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Renders subject+body for a culture (fallback: exact → parent → invariant). A token
    /// in the template with no value REFUSES — a half-rendered notice is worse than none.
    /// </summary>
    public (string? Subject, string Body, string Culture) Render(string culture, IReadOnlyDictionary<string, string> tokens)
    {
        var resolved = Resolve(culture);
        return (resolved.Value.Subject is null ? null : ReplaceTokens(resolved.Value.Subject, tokens),
            ReplaceTokens(resolved.Value.Body, tokens), resolved.Culture);
    }

    private (string Culture, (string? Subject, string Body) Value) Resolve(string culture)
    {
        for (var probe = culture; ; probe = ParentCulture(probe))
        {
            if (Cultures.TryGetValue(probe, out var value))
            {
                return (probe, value);
            }

            if (probe.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No template text for culture '{culture}' and no invariant fallback — register Subject/Body with culture \"\".");
            }
        }
    }

    private static string ParentCulture(string culture)
    {
        var dash = culture.LastIndexOf('-');
        return dash < 0 ? "" : culture[..dash];
    }

    internal static string ReplaceTokens(string text, IReadOnlyDictionary<string, string> tokens)
    {
        var result = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf("{{", i, StringComparison.Ordinal);
            if (open < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }

            var close = text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append(text, i, text.Length - i);
                break;
            }

            result.Append(text, i, open - i);
            var token = text[(open + 2)..close].Trim();
            if (!tokens.TryGetValue(token, out var value))
            {
                throw new InvalidOperationException(
                    $"Template token '{{{{{token}}}}}' has no value — a half-rendered notice never persists (supply the token or fix the template).");
            }

            result.Append(value);
            i = close + 2;
        }

        return result.ToString();
    }
}

/// <summary>Fluent registration for one channel of one template.</summary>
public sealed class GoldpathChannelTemplateBuilder
{
    private readonly GoldpathChannelTemplate _template;

    internal GoldpathChannelTemplateBuilder(GoldpathChannelTemplate template) => _template = template;

    /// <summary>Registers the subject for a culture ("" = invariant fallback).</summary>
    public GoldpathChannelTemplateBuilder Subject(string culture, string text)
    {
        var existing = _template.Cultures.TryGetValue(culture, out var value) ? value : (null, "");
        _template.Cultures[culture] = (text, existing.Item2);
        return this;
    }

    /// <summary>Registers the body for a culture ("" = invariant fallback).</summary>
    public GoldpathChannelTemplateBuilder Body(string culture, string text)
    {
        var existing = _template.Cultures.TryGetValue(culture, out var value) ? value : (null, "");
        _template.Cultures[culture] = (existing.Item1, text);
        return this;
    }
}

/// <summary>Fluent registration for one template.</summary>
public sealed class GoldpathNotificationTemplateBuilder
{
    private readonly GoldpathNotificationTemplate _template;

    internal GoldpathNotificationTemplateBuilder(GoldpathNotificationTemplate template) => _template = template;

    /// <summary>Registers the texts for one channel.</summary>
    public GoldpathNotificationTemplateBuilder Channel(string name, Action<GoldpathChannelTemplateBuilder> configure)
    {
        if (!_template.Channels.TryGetValue(name, out var channel))
        {
            channel = new GoldpathChannelTemplate();
            _template.Channels[name] = channel;
        }

        configure(new GoldpathChannelTemplateBuilder(channel));
        return this;
    }

    /// <summary>Nulls rendered bodies + attachments after this window (the evidence row survives).</summary>
    public GoldpathNotificationTemplateBuilder DeleteBodyAfter(TimeSpan period)
    {
        _template.DeleteBodyAfter = period;
        return this;
    }

    internal void Bake()
    {
        if (_template.Channels.Count == 0)
        {
            throw new InvalidOperationException($"Template '{_template.Key}' registers no channel — a template that cannot send is a typo.");
        }

        foreach (var (channelName, channel) in _template.Channels)
        {
            foreach (var (culture, value) in channel.Cultures)
            {
                if (value.Body.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Template '{_template.Key}' channel '{channelName}' culture '{culture}' has a subject but no body.");
                }
            }
        }

        // The what-was-sent proof: a canonical hash over every registered text.
        var canonical = new StringBuilder(_template.Key);
        foreach (var (channelName, channel) in _template.Channels.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            foreach (var (culture, value) in channel.Cultures.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                canonical.Append('\n').Append(channelName).Append('\n').Append(culture)
                    .Append('\n').Append(value.Subject).Append('\n').Append(value.Body);
            }
        }

        _template.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }
}

/// <summary>SMTP options for the shipped email channel (config-bound under Goldpath:Notification:Email).</summary>
public sealed class GoldpathEmailOptions
{
    /// <summary>SMTP host.</summary>
    public string Host { get; set; } = "";

    /// <summary>SMTP port (587 submission default).</summary>
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS toggle — secure BY DEFAULT (audit A3); see <see cref="AllowInsecureTransport"/>.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// EXPLICIT opt-out for plaintext SMTP (dev relays like MailHog): with
    /// <see cref="UseSsl"/> false and this false, the channel refuses to connect —
    /// credentials never travel in the clear by accident.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }

    /// <summary>SMTP user (empty = anonymous, dev relays).</summary>
    public string User { get; set; } = "";

    /// <summary>SMTP password.</summary>
    public string Password { get; set; } = "";

    /// <summary>The From address every message carries.</summary>
    public string From { get; set; } = "";
}

/// <summary>Options for the shipped webhook channel (config-bound under Goldpath:Notification:Webhook).</summary>
public sealed class GoldpathWebhookOptions
{
    /// <summary>The URL messages POST to (Teams/Slack/in-house hub). It must be an absolute <c>https</c> URL; see <see cref="AllowInsecureTransport"/>.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// EXPLICIT opt-in for a plaintext <c>http</c> URL (#533, threat model T-NTF-07), mirroring the email
    /// channel's <see cref="GoldpathEmailOptions.AllowInsecureTransport"/>: the posted JSON carries the recipient unmasked
    /// and the rendered message, so without this the channel refuses any URL that is not <c>https</c>, loopback included.
    /// Any scheme other than <c>http</c> and <c>https</c> is refused either way.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}

/// <summary>
/// Notification composition options (notification RFC §4). Templates bake at registration
/// (hash-stamped); the notifier, the send job and the admin surface stay non-generic.
/// </summary>
public sealed class GoldpathNotificationOptions
{
    internal Dictionary<string, GoldpathNotificationTemplate> TemplateMap { get; } = new(StringComparer.Ordinal);

    /// <summary>The registered templates.</summary>
    public IReadOnlyCollection<GoldpathNotificationTemplate> Templates => TemplateMap.Values;

    /// <summary>Shipped email channel options.</summary>
    public GoldpathEmailOptions EmailOptions { get; } = new();

    /// <summary>Shipped webhook channel options.</summary>
    public GoldpathWebhookOptions WebhookOptions { get; } = new();

    /// <summary>Send attempts per notification before it fails into the repair queue (D6).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Backoff between attempts inside one send pass.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Notifications per send chunk (each chunk is one checkpoint).</summary>
    public int ChunkSize { get; set; } = 100;

    /// <summary>A claim older than this with no outcome is an interrupted send (crash sweep).</summary>
    public TimeSpan StaleClaimAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The app's opt-out hook (D5): false → the request lands as Suppressed EVIDENCE.</summary>
    public Func<GoldpathNotificationRequest, IServiceProvider, Task<bool>> MaySendHook { get; private set; }
        = static (_, _) => Task.FromResult(true);

    /// <summary>Registers one template.</summary>
    public GoldpathNotificationOptions AddTemplate(string key, Action<GoldpathNotificationTemplateBuilder> configure)
    {
        if (TemplateMap.ContainsKey(key))
        {
            throw new InvalidOperationException($"Template '{key}' is already registered.");
        }

        var template = new GoldpathNotificationTemplate(key);
        var builder = new GoldpathNotificationTemplateBuilder(template);
        configure(builder);
        builder.Bake();
        TemplateMap[key] = template;
        return this;
    }

    /// <summary>Configures the shipped email channel.</summary>
    public GoldpathNotificationOptions Email(Action<GoldpathEmailOptions> configure)
    {
        configure(EmailOptions);
        return this;
    }

    /// <summary>Configures the shipped webhook channel.</summary>
    public GoldpathNotificationOptions Webhook(Action<GoldpathWebhookOptions> configure)
    {
        configure(WebhookOptions);
        return this;
    }

    /// <summary>Registers the opt-out hook.</summary>
    public GoldpathNotificationOptions MaySend(Func<GoldpathNotificationRequest, IServiceProvider, Task<bool>> hook)
    {
        MaySendHook = hook;
        return this;
    }

    /// <summary>Finds a template or fails with a teaching message.</summary>
    public GoldpathNotificationTemplate Template(string key)
        => TemplateMap.TryGetValue(key, out var template)
            ? template
            : throw new InvalidOperationException(
                $"No template named '{key}' — registered: {string.Join(", ", TemplateMap.Keys)}.");
}

/// <summary>One notification request (the app's side of the D2 contract).</summary>
public sealed class GoldpathNotificationRequest
{
    /// <summary>Creates a request; the dedup key is REQUIRED — idempotence is not optional.</summary>
    public GoldpathNotificationRequest(
        string template, string channel, string recipient, string culture,
        IReadOnlyDictionary<string, string> tokens, string dedupKey)
    {
        if (string.IsNullOrWhiteSpace(dedupKey))
        {
            throw new ArgumentException(
                "A notification needs its dedup key — the business identity that makes a retry storm land ONCE (for example: \"renewal:P-42:2026-08\").",
                nameof(dedupKey));
        }

        Template = template;
        Channel = channel;
        Recipient = recipient;
        Culture = culture;
        Tokens = tokens;
        DedupKey = dedupKey;
    }

    /// <summary>The registered template key.</summary>
    public string Template { get; }

    /// <summary>The channel to send through.</summary>
    public string Channel { get; }

    /// <summary>The destination address.</summary>
    public string Recipient { get; }

    /// <summary>The requested culture (fallback chain applies).</summary>
    public string Culture { get; }

    /// <summary>Token values the template renders with.</summary>
    public IReadOnlyDictionary<string, string> Tokens { get; }

    /// <summary>The unique business identity of this notification.</summary>
    public string DedupKey { get; }

    /// <summary>Earliest send time (quiet hours are a field, not a policy engine).</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>Owning tenant, when tenant-bound.</summary>
    public string? Tenant { get; init; }

    /// <summary>Caller's correlation id (per-instruction tracing).</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Attachments (the policy PDF); retention nulls content, names survive.</summary>
    public IReadOnlyList<GoldpathNotificationAttachmentContent> Attachments { get; init; } = [];
}
