using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goldpath.Notification.Tests;

/// <summary>
/// #532 (threat T-NTF-09): a channel's exception text is the provider's own words, and providers name who they refused.
/// The recipient is masked on every admin surface, so the text that reaches <c>Detail</c>, the jobs console's repair
/// reason and the log is masked before any of them sees it, fail-closed over the class: the recipient in any letter
/// case, percent-encoded, its local part alone, ANOTHER address, a number in any format or script, a URL's credentials
/// and query, a forged log line, and a text longer than the column.
/// </summary>
public sealed class FailureDetailMaskingTests : IDisposable
{
    private const string Email = "omer@example.test";
    private const string Phone = "+905551112233";

    private readonly NotificationFixture _fixture = new();
    private readonly GoldpathNotificationAdminService<NotificationTestContext> _admin;

    public FailureDetailMaskingTests()
        => _admin = new GoldpathNotificationAdminService<NotificationTestContext>(
            _fixture.Options, _fixture.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

    public void Dispose() => _fixture.Dispose();

    private static GoldpathNotificationRequest To(string recipient, string dedupKey)
        => new("policy-renewal", "recording", recipient, "tr",
            new Dictionary<string, string> { ["Name"] = "FAIL", ["PolicyNo"] = "P-42", ["RenewalDate"] = "2026-08-01" }, dedupKey);

    /// <summary>Fails one notice to <paramref name="recipient"/> with <paramref name="providerText"/>; answers every surface's text.</summary>
    private async Task<(string Detail, string Reason, string Failures, string One)> FailAsync(string recipient, string providerText)
    {
        _fixture.Channel.FailureMessage = providerText;
        var row = await _fixture.RequestAsync(To(recipient, $"k:{Guid.NewGuid():N}"));
        var reason = Assert.Single(await _fixture.RunSendPassAsync()).Reason;
        var detail = _fixture.Query(db => db.Set<GoldpathNotification>().AsNoTracking().Single(n => n.Id == row.Id)).Detail!;
        var failures = Assert.Single(await _admin.GetFailuresAsync(null, 50, CancellationToken.None), f => f.Id == row.Id).Detail!;
        var one = (await _admin.GetNotificationAsync(row.Id, null, CancellationToken.None))!.Detail!;
        return (detail, reason, failures, one);
    }

    [Fact]
    public async Task An_smtp_reply_naming_the_recipient_reaches_no_surface_unmasked()
    {
        var (detail, reason, failures, one) = await FailAsync(Email, $"550 5.1.1 <{Email}>: Recipient address rejected: User unknown");

        foreach (var shown in new[] { detail, reason, failures, one })
        {
            Assert.DoesNotContain(Email, shown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("omer", shown, StringComparison.OrdinalIgnoreCase);
            // The diagnosis survives: the provider's code and words, and the mask's triage hint.
            Assert.Equal("550 5.1.1 <o***@e***>: Recipient address rejected: User unknown", shown);
        }
    }

    [Theory]
    [InlineData(Email, "550 <OMER@EXAMPLE.TEST>: rejected", "omer@example.test")]   // another letter case
    [InlineData(Email, "rejected recipient omer%40example.test", "omer%40example.test")]   // percent-encoded
    [InlineData(Email, "mailbox omer unavailable", "omer")]   // the local part alone
    [InlineData(Email, "mailbox OMER unavailable", "omer")]   // the local part alone, in another case
    [InlineData("Device-Token-AbC", "push: token device-token-abc is unregistered", "device-token-abc")]   // neither an address nor a number
    [InlineData(Email, "forwarding to someone.else@other.test failed", "someone.else@other.test")]   // an address the row does not hold
    [InlineData(Phone, "21211 invalid To number +905551112233", "905551112233")]
    [InlineData(Phone, "number +90 555 111 22 33 is unreachable", "555 111 22 33")]   // formatted
    [InlineData(Phone, "number 0555-111-22-33 is unreachable", "555-111-22-33")]   // national format
    [InlineData(Phone, "number (0555) 111.22.33 opted out", "111.22.33")]
    [InlineData(Phone, "الرقم ٩٠٥٥٥١١١٢٢٣٣ غير صالح", "٥٥٥١١١٢٢٣٣")]   // Arabic-Indic digits
    [InlineData(Email, "POST https://ops:s3cr3t@hooks.example/send?token=abc123 failed", "s3cr3t")]   // URL credentials
    [InlineData(Email, "POST https://hooks.example/send?token=abc123 failed", "abc123")]   // URL query
    public async Task Every_shape_of_a_recipient_or_a_credential_in_a_providers_text_is_masked(string recipient, string providerText, string mustNotShow)
    {
        var (detail, reason, failures, one) = await FailAsync(recipient, providerText);
        foreach (var shown in new[] { detail, reason, failures, one })
        {
            Assert.DoesNotContain(mustNotShow, shown, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(Email, "rejected recipient omer%40example.test", "rejected recipient o***@e***")]
    [InlineData(Email, "mailbox omer unavailable", "mailbox o*** unavailable")]
    [InlineData("ali@example.test", "mailbox ali unavailable", "mailbox a*** unavailable")]   // the shortest local part masked alone: three
    [InlineData("al@example.test", "mailbox al unavailable", "mailbox al unavailable")]   // two letters would mask every "al" in a sentence
    [InlineData(Phone, "number +90 555 111 22 33 is unreachable", "number +*** is unreachable")]
    [InlineData(Phone, "code 550 5.1.1 for +905551112233", "code 550 5.1.1 for +***")]   // an SMTP status (six digits) is kept
    [InlineData(Phone, "number +1 (555) 123-4567 is unreachable", "number +*** is unreachable")]   // a parenthesised area code is no break
    [InlineData(Phone, "number +90 (555) 111 22 33 opted out", "number +*** opted out")]
    [InlineData(Email, "POST https://hooks.example/send failed", "POST https://hooks.example/send failed")]   // nothing to remove
    [InlineData(Email, "POST https://hooks.example/send#frag failed", "POST https://hooks.example/send?*** failed")]
    [InlineData(Email, "connect http://hooks.example:99999/x refused", "connect *** refused")]   // a URL that does not parse is withheld whole
    public async Task The_masked_text_keeps_the_diagnosis_and_only_the_diagnosis(string recipient, string providerText, string expected)
    {
        var (detail, reason, failures, one) = await FailAsync(recipient, providerText);
        Assert.Equal([expected, expected, expected, expected], new[] { detail, reason, failures, one });
    }

    [Fact]
    public async Task A_huge_text_is_cut_at_its_last_word_before_the_read_limit_so_no_half_token_is_masked_or_shown()
    {
        // A URL with a long query straddles the read limit: cut at the space before it, never through it.
        var straddling = new string('x', 500) + " https://hooks.example/p?" + new string('q', 9_000) + $" <{Email}>";
        var (detail, _, _, _) = await FailAsync(Email, straddling);
        Assert.Equal(new string('x', 500) + " …", detail);

        // No whitespace at all: cut at the limit itself, and still bounded.
        var (solid, _, _, _) = await FailAsync(Email, new string('w', 10_000));
        Assert.Equal(new string('w', 1023) + "…", solid);

        // Its only whitespace is its first character: that is no word boundary to cut at, so the limit is the cut.
        var (leading, _, _, _) = await FailAsync(Email, " " + new string('w', 10_000));
        Assert.Equal(" " + new string('w', 1022) + "…", leading);
    }

    /// <summary>What a strict encoder (Npgsql's) does with the text: throws on a lone surrogate, as the save would.</summary>
    private static void AssertWellFormed(string shown)
        => _ = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(shown);

    [Fact]
    public async Task No_cut_falls_between_a_surrogate_pair_at_the_column_or_at_the_read_limit()
    {
        // A non-BMP character straddling the column's cut, at every offset around it (the verifier's probe is 1,022).
        for (var at = 1_018; at <= 1_026; at++)
        {
            var (detail, reason, failures, one) = await FailAsync(Email, new string('a', at) + "\U0001F600" + new string('b', 50));
            foreach (var shown in new[] { detail, reason, failures, one })
            {
                AssertWellFormed(shown);
                Assert.True(shown.Length <= 1024);
                Assert.EndsWith("…", shown, StringComparison.Ordinal);
            }
        }

        // The same at the read limit, where a text without whitespace is cut at the limit itself.
        for (var at = 8_189; at <= 8_192; at++)
        {
            var (detail, _, _, _) = await FailAsync(Email, new string('w', at) + "\U0001F600" + new string('w', 2_000));
            AssertWellFormed(detail);
        }

        // Kept whole when it fits exactly: a pair ending at the column is not cut.
        var (fits, _, _, _) = await FailAsync(Email, new string('a', 1022) + "\U0001F600");
        Assert.Equal(new string('a', 1022) + "\U0001F600", fits);
    }

    [Theory]
    [InlineData("\U0001F600token", "push: token \U0001F600token unregistered", "push: token \U0001F600*** unregistered")]   // a token led by a non-BMP character
    [InlineData("\U0001D49Cmer@\U0001D49Cxample.test", "550 <\U0001D49Cmer@\U0001D49Cxample.test> rejected", "550 <\U0001D49C***@\U0001D49C***> rejected")]   // an EAI address, both hints
    [InlineData(Email, "550 <\U0001D49Cther@\U0001F600mail.test> rejected", "550 <\U0001D49C***@\U0001F600***> rejected")]   // an address the row does not hold
    public async Task Every_kept_hint_is_a_whole_character_and_a_lone_surrogate_never_survives(string recipient, string providerText, string expected)
    {
        var (detail, reason, failures, one) = await FailAsync(recipient, providerText);
        foreach (var shown in new[] { detail, reason, failures, one })
        {
            AssertWellFormed(shown);
            Assert.Equal(expected, shown);
        }

        AssertWellFormed(GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient(recipient));
    }

    [Fact]
    public async Task A_lone_surrogate_in_the_providers_text_or_leading_a_recipient_becomes_a_replacement_character()
    {
        // Built at run time: a lone half in an attribute's string does not survive the compiled metadata.
        var high = char.ConvertFromUtf32(0x1F600)[0].ToString();
        var low = char.ConvertFromUtf32(0x1F600)[1].ToString();
        foreach (var (text, expected) in new[]
                 {
                     ($"refused {high} here and {low} there", "refused \uFFFD here and \uFFFD there"),   // the provider's own lone halves
                     ($"refused {low}{high} reversed", "refused \uFFFD\uFFFD reversed"),   // a low before a high is no pair
                     ($"refused at the end {high}", "refused at the end \uFFFD"),
                 })
        {
            var (detail, reason, failures, one) = await FailAsync(Email, text);
            foreach (var shown in new[] { detail, reason, failures, one })
            {
                AssertWellFormed(shown);
                Assert.Equal(expected, shown);
            }
        }

        // A recipient whose first character is a lone half: the hint is U+FFFD, on the recipient column and in the text.
        var token = low + "token";
        Assert.Equal("\uFFFD***", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient(token));
        var (masked, _, _, _) = await FailAsync(token, $"push: token {token} unregistered");
        Assert.Equal("push: token \uFFFD*** unregistered", masked);

        // SQLite stores UTF-8, so the row above came back with U+FFFD on both sides. A store that keeps UTF-16 as it is
        // (SQL Server's nvarchar) hands the mask the lone half itself, in the recipient as well as the text; the mask
        // sanitises both before matching, or the recipient would no longer match itself. Called directly, as the job does.
        var mask = typeof(GoldpathNotificationAdminService<>).Assembly.GetType("Goldpath.GoldpathNotificationFailureText", throwOnError: true)!
            .GetMethod("Mask", [typeof(string), typeof(string)])!;
        Assert.Equal("push: token \uFFFD*** unregistered", (string)mask.Invoke(null, [$"push: token {token} unregistered", token])!);
    }

    [Fact]
    public async Task A_row_without_a_detail_shows_none_and_an_empty_recipient_does_not_break_the_view()
    {
        var sent = await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:fine"));
        await _fixture.RunSendPassAsync();
        Assert.Null((await _admin.GetNotificationAsync(sent.Id, null, CancellationToken.None))!.Detail);

        _fixture.Mutate(db =>
        {
            var odd = db.Set<GoldpathNotification>().Single(n => n.Id == sent.Id);
            odd.Recipient = "";
            odd.Detail = "refused by +905551112233";
        });
        Assert.Equal("refused by +***", (await _admin.GetNotificationAsync(sent.Id, null, CancellationToken.None))!.Detail);
        Assert.Equal("@***", GoldpathNotificationAdminService<NotificationTestContext>.MaskRecipient("@example.test"));   // no local part
    }

    [Fact]
    public async Task A_url_keeps_its_host_and_path_and_says_its_credentials_were_removed()
    {
        var (detail, _, _, _) = await FailAsync(Email, "POST https://ops:s3cr3t@hooks.example/send?token=abc123 failed");
        Assert.Equal("POST https://hooks.example/send?*** (credentials removed) failed", detail);
    }

    [Fact]
    public async Task A_provider_text_is_one_line_so_it_cannot_forge_a_log_entry()
    {
        var (detail, reason, _, _) = await FailAsync(Email, "refused\r\n2026-11-21 info: Notification sent OK\u2028\u0000next");
        Assert.DoesNotContain('\n', detail);
        Assert.DoesNotContain('\r', reason);
        Assert.DoesNotContain('\u2028', detail);
        Assert.DoesNotContain('\0', detail);
        Assert.StartsWith("refused  ", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_text_longer_than_the_column_is_cut_to_it_and_marked_and_a_huge_one_is_read_only_in_part()
    {
        var (detail, reason, _, _) = await FailAsync(Email, new string('x', 900) + " " + new string('y', 900));
        Assert.Equal(1024, detail.Length);   // NotificationModel: Detail HasMaxLength(1024)
        Assert.EndsWith("…", detail, StringComparison.Ordinal);
        Assert.Equal(detail, reason);

        // A megabyte with the recipient at its far end: bounded, and the recipient never surfaces, cut or not.
        var (huge, _, _, _) = await FailAsync(Email, string.Join(' ', Enumerable.Repeat("filler", 200_000)) + $" <{Email}>");
        Assert.True(huge.Length <= 1024);
        Assert.DoesNotContain("omer", huge, StringComparison.OrdinalIgnoreCase);

        // Exactly the column: kept whole, unmarked.
        var (exact, _, _, _) = await FailAsync(Email, new string('z', 1024));
        Assert.Equal(new string('z', 1024), exact);
    }

    [Fact]
    public async Task A_row_that_failed_before_the_upgrade_is_masked_on_the_way_out()
    {
        var row = await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:legacy"));
        _fixture.Mutate(db =>
        {
            var legacy = db.Set<GoldpathNotification>().Single(n => n.Id == row.Id);
            legacy.State = GoldpathNotificationState.Failed;
            legacy.Detail = $"550 5.1.1 <{Email}>: Recipient address rejected";   // what the send job used to store
        });

        Assert.Equal("550 5.1.1 <o***@e***>: Recipient address rejected",
            Assert.Single(await _admin.GetFailuresAsync(null, 50, CancellationToken.None)).Detail);
        Assert.Equal("550 5.1.1 <o***@e***>: Recipient address rejected",
            (await _admin.GetNotificationAsync(row.Id, null, CancellationToken.None))!.Detail);
        Assert.Contains(await _admin.GetNotificationsAsync(null, null, null, 50, CancellationToken.None),
            n => n.Id == row.Id && n.Detail == "550 5.1.1 <o***@e***>: Recipient address rejected");
    }

    [Fact]
    public async Task The_portals_own_teaching_texts_pass_through_the_mask_unchanged()
    {
        // The interrupted-claim text and a suppression are the module's words; masking must not garble them.
        var interrupted = await _fixture.RequestAsync(NotificationFixture.Renewal("renewal:cut"));
        _fixture.Mutate(db => db.Set<GoldpathNotification>().Single().ClaimedAt = DateTimeOffset.UtcNow.AddMinutes(-10));
        await _fixture.RunSendPassAsync();
        Assert.Equal("interrupted mid-flight on a previous attempt — confirm with the provider, then replay",
            (await _admin.GetNotificationAsync(interrupted.Id, null, CancellationToken.None))!.Detail);
    }

    [Fact]
    public async Task The_log_line_carries_the_masked_text_and_the_failure_type_never_the_exception()
    {
        var logger = new CapturingLogger();
        _fixture.Channel.FailureMessage = $"550 5.1.1 <{Email}>: Recipient address rejected";
        await _fixture.RequestAsync(To(Email, "k:log"));
        using var scope = _fixture.Services.CreateScope();
        var job = new GoldpathNotificationSendJob<NotificationTestContext>(_fixture.Options, [_fixture.Channel], TimeProvider.System, logger);
        var context = NotificationFixture.CreateContext(scope.ServiceProvider);
        var plan = await job.PlanAsync(context, CancellationToken.None);
        for (var i = 0; i < plan.ChunkPayloads.Count; i++)
        {
            await job.ExecuteChunkAsync(NotificationFixture.MakeChunk(i, plan.ChunkPayloads[i]), context, CancellationToken.None);
        }

        var (level, message, exception) = Assert.Single(logger.Entries, e => e.Message.Contains("exhausted", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Null(exception);   // the exception object carries the unmasked message (and its inner ones)
        Assert.DoesNotContain("omer", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System.InvalidOperationException", message, StringComparison.Ordinal);
        Assert.Contains("<o***@e***>: Recipient address rejected", message, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<GoldpathNotificationSendJob<NotificationTestContext>>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
