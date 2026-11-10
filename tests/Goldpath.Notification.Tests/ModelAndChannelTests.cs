using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Goldpath.Notification.Tests;

/// <summary>The store schema is a wire contract; the email channel's MIME build is testable without a server.</summary>
public class ModelAndChannelTests : IDisposable
{
    private readonly NotificationFixture _fixture = new();
    private readonly IModel _model;

    public ModelAndChannelTests()
        => _model = FreshModel();

    /// <summary>
    /// EF caches the model per context type in a STATIC cache — under Stryker's
    /// mutation-switching (all mutants in one assembly, toggled at runtime) a cached
    /// model would make OnModelCreating mutants invisible. Service-provider caching off
    /// forces a REAL model build per activation.
    /// </summary>
    private static IModel FreshModel()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<NotificationTestContext>()
            .UseSqlite(connection)
            .EnableServiceProviderCaching(false)
            .Options;
        using var db = new NotificationTestContext(options);
        return db.Model;
    }

    public void Dispose() => _fixture.Dispose();

    private IEntityType Entity<T>() => _model.FindEntityType(typeof(T))!;

    [Fact]
    public void Tables_keys_and_the_dedup_identity_are_the_documented_shapes()
    {
        Assert.Equal("GoldpathNotifications", Entity<GoldpathNotification>().GetTableName());
        Assert.Equal("GoldpathNotificationAttachments", Entity<GoldpathNotificationAttachment>().GetTableName());
        Assert.Equal(ValueGenerated.Never, Entity<GoldpathNotification>().FindProperty("Id")!.ValueGenerated);

        var dedup = Entity<GoldpathNotification>().GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == "DedupKey");
        Assert.True(dedup.IsUnique, "the dedup key IS the idempotence — uniqueness is the race guard");
        Assert.Contains(Entity<GoldpathNotification>().GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(["State", "RequestedAt"]));   // the send claim query
        Assert.True(Entity<GoldpathNotification>().FindProperty("State")!.IsConcurrencyToken);
    }

    [Fact]
    public void Bounded_lengths_serve_both_providers()
    {
        var notification = Entity<GoldpathNotification>();
        Assert.Equal(256, notification.FindProperty("DedupKey")!.GetMaxLength());
        Assert.Equal(128, notification.FindProperty("Template")!.GetMaxLength());
        Assert.Equal(64, notification.FindProperty("TemplateHash")!.GetMaxLength());
        Assert.Equal(64, notification.FindProperty("Channel")!.GetMaxLength());
        Assert.Equal(512, notification.FindProperty("Recipient")!.GetMaxLength());
        Assert.Equal(16, notification.FindProperty("Culture")!.GetMaxLength());
        Assert.Equal(1024, notification.FindProperty("Subject")!.GetMaxLength());
        Assert.Equal(1024, notification.FindProperty("Detail")!.GetMaxLength());
        Assert.Equal(260, Entity<GoldpathNotificationAttachment>().FindProperty("Name")!.GetMaxLength());
        Assert.Equal(128, notification.FindProperty("Tenant")!.GetMaxLength());
        Assert.Equal(128, notification.FindProperty("CorrelationId")!.GetMaxLength());
        Assert.Equal(128, Entity<GoldpathNotificationAttachment>().FindProperty("ContentType")!.GetMaxLength());
    }

    [Fact]
    public void Every_index_is_the_documented_shape()
    {
        var indexes = Entity<GoldpathNotification>().GetIndexes()
            .Select(i => (Props: string.Join("+", i.Properties.Select(p => p.Name)), i.IsUnique))
            .OrderBy(i => i.Props, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
        [
            ("DedupKey", true),
            ("State+RequestedAt", false),
            ("Template+RequestedAt", false),
        ], indexes);

        var attachment = Assert.Single(Entity<GoldpathNotificationAttachment>().GetIndexes());
        Assert.Equal(["NotificationId"], attachment.Properties.Select(p => p.Name));
        Assert.False(attachment.IsUnique);
    }

    [Fact]
    public void Entity_defaults_are_the_documented_blank_slate()
    {
        // Property initializers are wire behavior: a mutated default would leak into
        // every INSERT. Golden-pinned.
        var notification = new GoldpathNotification();
        Assert.Equal(Guid.Empty, notification.Id);
        Assert.Equal("", notification.DedupKey);
        Assert.Equal("", notification.Template);
        Assert.Equal("", notification.TemplateHash);
        Assert.Equal("", notification.Channel);
        Assert.Equal("", notification.Recipient);
        Assert.Equal("", notification.Culture);
        Assert.Null(notification.Subject);
        Assert.Null(notification.Body);
        Assert.Null(notification.BodyDeletedAt);
        Assert.Equal(GoldpathNotificationState.Requested, notification.State);
        Assert.Null(notification.NotBefore);
        Assert.Null(notification.ClaimedAt);
        Assert.Null(notification.SentAt);
        Assert.Null(notification.FailedAt);
        Assert.Equal(0, notification.Attempts);
        Assert.Null(notification.Detail);
        Assert.Null(notification.Tenant);
        Assert.Null(notification.CorrelationId);

        var attachment = new GoldpathNotificationAttachment();
        Assert.Equal(0, attachment.Id);
        Assert.Equal(Guid.Empty, attachment.NotificationId);
        Assert.Equal("", attachment.Name);
        Assert.Equal("", attachment.ContentType);
        Assert.Null(attachment.Content);
    }

    [Fact]
    public void State_values_are_wire_stable()
    {
        Assert.Equal(0, (int)GoldpathNotificationState.Requested);
        Assert.Equal(1, (int)GoldpathNotificationState.Suppressed);
        Assert.Equal(2, (int)GoldpathNotificationState.Sent);
        Assert.Equal(3, (int)GoldpathNotificationState.Failed);
    }

    [Fact]
    public void The_email_channel_builds_the_mime_message_with_attachments_and_the_evidence_id()
    {
        var options = new GoldpathNotificationOptions();
        options.Email(e => { e.Host = "smtp.local"; e.From = "noreply@goldpath.local"; });
        var channel = new GoldpathEmailChannel(options);
        var id = Guid.NewGuid();

        var mime = channel.BuildMessage(new GoldpathNotificationMessage(
            id, "omer@example.test", "Konu", "Gövde",
            [new GoldpathNotificationAttachmentContent("policy.pdf", "application/pdf", [1, 2, 3])],
            "policy-renewal", null, "corr-1"));

        Assert.Equal("Konu", mime.Subject);
        Assert.Equal("noreply@goldpath.local", mime.From.ToString());
        Assert.Equal("omer@example.test", mime.To.ToString());
        Assert.Contains($"{id:N}@goldpath", mime.MessageId, StringComparison.Ordinal);   // the evidence id travels
        Assert.Contains("policy.pdf", mime.BodyParts.Select(p => p.ContentDisposition?.FileName));
    }

    /// <summary>
    /// Threat model T-NTF-04: a subject is a header VALUE. A line break in it, from a token, never
    /// starts a header of its own, so a rendered subject cannot add a <c>Bcc</c>.
    /// </summary>
    [Fact]
    public void A_line_break_in_the_subject_never_becomes_a_header()
    {
        var options = new GoldpathNotificationOptions();
        options.Email(e => { e.Host = "smtp.local"; e.From = "noreply@goldpath.local"; });

        var mime = new GoldpathEmailChannel(options).BuildMessage(new GoldpathNotificationMessage(
            Guid.NewGuid(), "omer@example.test", "Konu\r\nBcc: someone@elsewhere.test", "Gövde", [], "t", null, null));
        using var wire = new MemoryStream();
        mime.WriteTo(wire);
        var headers = System.Text.Encoding.UTF8.GetString(wire.ToArray()).Split("\r\n\r\n")[0].Split("\r\n");

        Assert.DoesNotContain(headers, h => h.StartsWith("Bcc:", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(mime.Bcc);
        Assert.Equal("omer@example.test", mime.To.ToString());
    }

    /// <summary>
    /// Threat model T-NTF-05: a notification goes to ONE person. A recipient that is an address
    /// list is refused when the message is built, never sent to everybody it names.
    /// </summary>
    [Fact]
    public void A_recipient_is_one_mailbox_and_a_list_is_refused()
    {
        var options = new GoldpathNotificationOptions();
        options.Email(e => { e.Host = "smtp.local"; e.From = "noreply@goldpath.local"; });
        var channel = new GoldpathEmailChannel(options);

        Assert.Throws<MimeKit.ParseException>(() => channel.BuildMessage(new GoldpathNotificationMessage(
            Guid.NewGuid(), "omer@example.test, someone@elsewhere.test", "Konu", "Gövde", [], "t", null, null)));
    }

    [Fact]
    public async Task Misconfigured_shipped_channels_teach_their_config_keys()
    {
        var options = new GoldpathNotificationOptions();
        var message = new GoldpathNotificationMessage(Guid.NewGuid(), "x", null, "b", [], "t", null, null);

        var email = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GoldpathEmailChannel(options).SendAsync(message, CancellationToken.None));
        Assert.Contains("Goldpath:Notification:Email", email.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plaintext_smtp_is_an_explicit_optout_never_a_default()
    {
        var options = new GoldpathNotificationOptions();
        options.Email(e => { e.Host = "smtp.local"; e.From = "noreply@goldpath.local"; });

        Assert.True(options.EmailOptions.UseSsl);   // secure BY DEFAULT (audit A3)

        options.Email(e => e.UseSsl = false);
        var message = new GoldpathNotificationMessage(Guid.NewGuid(), "x@y.z", null, "b", [], "t", null, null);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new GoldpathEmailChannel(options).SendAsync(message, CancellationToken.None));
        Assert.Contains("AllowInsecureTransport", refused.Message, StringComparison.Ordinal);
    }
}
