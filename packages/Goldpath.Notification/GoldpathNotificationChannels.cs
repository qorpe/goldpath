using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Goldpath;

/// <summary>
/// The shipped email channel: MailKit SMTP (MIT), config-bound. Returning normally means
/// the SMTP server ACCEPTED the message — accepted, not delivered (RFC honesty).
/// </summary>
public sealed class GoldpathEmailChannel : IGoldpathNotificationChannel
{
    private readonly GoldpathEmailOptions _options;

    /// <summary>Registered by <c>AddGoldpathNotification</c>.</summary>
    public GoldpathEmailChannel(GoldpathNotificationOptions options) => _options = options.EmailOptions;

    /// <inheritdoc />
    public string Name => "email";

    /// <inheritdoc />
    public async Task SendAsync(GoldpathNotificationMessage message, CancellationToken cancellationToken)
    {
        if (_options.Host.Length == 0 || _options.From.Length == 0)
        {
            throw new InvalidOperationException(
                "The email channel needs Goldpath:Notification:Email Host and From — configuration, not code (see the notification README).");
        }

        if (!_options.UseSsl && !_options.AllowInsecureTransport)
        {
            // Fail-closed (audit A3): plaintext SMTP is a visible decision, never a default.
            throw new InvalidOperationException(
                "Goldpath:Notification:Email:UseSsl is false without AllowInsecureTransport=true — plaintext SMTP must be opted into explicitly (dev relays only).");
        }

        var mime = BuildMessage(message);
        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port,
            _options.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken);
        if (_options.User.Length > 0)
        {
            await client.AuthenticateAsync(_options.User, _options.Password, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }

    /// <summary>Builds the MIME message (unit-testable without a server).</summary>
    public MimeMessage BuildMessage(GoldpathNotificationMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(_options.From));
        mime.To.Add(MailboxAddress.Parse(message.Recipient));
        mime.Subject = message.Subject ?? "";
        var builder = new BodyBuilder { TextBody = message.Body };
        foreach (var attachment in message.Attachments)
        {
            builder.Attachments.Add(attachment.Name, attachment.Content, ContentType.Parse(attachment.ContentType));
        }

        mime.Body = builder.ToMessageBody();
        mime.MessageId = $"<{message.NotificationId:N}@goldpath>";   // the evidence id travels with the mail
        return mime;
    }
}

/// <summary>
/// The shipped webhook channel: POST JSON to a bound URL (Teams/Slack/in-house hubs).
/// Attachment CONTENT is ignored by design (a hook is not a file store); names are listed.
/// </summary>
public sealed class GoldpathWebhookChannel : IGoldpathNotificationChannel
{
    private readonly GoldpathWebhookOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Registered by <c>AddGoldpathNotification</c>.</summary>
    public GoldpathWebhookChannel(GoldpathNotificationOptions options, IHttpClientFactory httpClientFactory)
    {
        _options = options.WebhookOptions;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "webhook";

    /// <inheritdoc />
    public async Task SendAsync(GoldpathNotificationMessage message, CancellationToken cancellationToken)
    {
        if (_options.Url.Length == 0)
        {
            throw new InvalidOperationException(
                "The webhook channel needs Goldpath:Notification:Webhook Url — configuration, not code.");
        }

        // Fail-closed (#533, T-NTF-07), as the email channel is on plaintext: the JSON below carries the recipient unmasked
        // and the rendered message, so it travels over TLS unless plaintext is a visible decision. The rule is an allow-list
        // of the parsed URI's scheme — https, and http only with the opt-in — so every other spelling (a relative URL, a
        // file path, ws:, ftp:, javascript:) is refused too. The URL is never echoed: a hub's secret is often in it (A11).
        if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var url)
            || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && _options.AllowInsecureTransport)))
        {
            throw new InvalidOperationException(
                "Goldpath:Notification:Webhook:Url is not an absolute https URL — a plaintext http hook must be opted into explicitly "
                + "with Goldpath:Notification:Webhook:AllowInsecureTransport=true, and no other scheme is accepted.");
        }

        var client = _httpClientFactory.CreateClient("goldpath-notification-webhook");
        var response = await client.PostAsJsonAsync(url, new
        {
            id = message.NotificationId,
            recipient = message.Recipient,
            subject = message.Subject,
            body = message.Body,
            attachments = message.Attachments.Select(a => a.Name).ToArray(),
            template = message.Template,
            correlationId = message.CorrelationId,
        }, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
