using Xunit;

namespace Goldpath.Notification.Tests;

public class TemplateTests
{
    private static GoldpathNotificationOptions Options()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("welcome", t => t
            .Channel("email", c => c
                .Subject("tr", "Hoş geldin {{Name}}")
                .Body("tr", "Merhaba {{Name}}, hesabınız hazır.")
                .Body("", "Hello {{Name}}, your account is ready.")));
        return options;
    }

    [Fact]
    public void Renders_tokens_for_the_exact_culture()
    {
        var (subject, body, culture) = Options().Template("welcome").ChannelTemplate("email")
            .Render("tr", new Dictionary<string, string> { ["Name"] = "Ömer" });
        Assert.Equal("Hoş geldin Ömer", subject);
        Assert.Equal("Merhaba Ömer, hesabınız hazır.", body);
        Assert.Equal("tr", culture);
    }

    [Fact]
    public void Falls_back_along_the_culture_chain_and_records_what_rendered()
    {
        var (subject, body, culture) = Options().Template("welcome").ChannelTemplate("email")
            .Render("de-DE", new Dictionary<string, string> { ["Name"] = "Ömer" });
        Assert.Null(subject);                                        // invariant has no subject
        Assert.Equal("Hello Ömer, your account is ready.", body);
        Assert.Equal("", culture);                                   // the evidence names the fallback

        var (_, trBody, trCulture) = Options().Template("welcome").ChannelTemplate("email")
            .Render("tr-TR", new Dictionary<string, string> { ["Name"] = "Ömer" });
        Assert.Equal("tr", trCulture);                               // tr-TR → tr
        Assert.Contains("Merhaba", trBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_token_refuses_with_a_teaching_message()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            Options().Template("welcome").ChannelTemplate("email").Render("tr", new Dictionary<string, string>()));
        Assert.Contains("{{Name}}", e.Message, StringComparison.Ordinal);
        Assert.Contains("half-rendered", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hash_is_stable_and_content_sensitive()
    {
        var first = Options().Template("welcome").Hash;
        var second = Options().Template("welcome").Hash;
        Assert.Equal(first, second);                                 // deterministic
        Assert.Equal(64, first.Length);

        var changed = new GoldpathNotificationOptions();
        changed.AddTemplate("welcome", t => t
            .Channel("email", c => c.Body("", "Hello {{Name}}, your account is READY.")));
        Assert.NotEqual(first, changed.Template("welcome").Hash);    // any text change = new proof
    }

    [Fact]
    public void Registration_refuses_typo_shapes()
    {
        var options = new GoldpathNotificationOptions();
        Assert.Contains("registers no channel",
            Assert.Throws<InvalidOperationException>(() => options.AddTemplate("empty", t => { })).Message,
            StringComparison.Ordinal);
        Assert.Contains("subject but no body",
            Assert.Throws<InvalidOperationException>(() => options.AddTemplate("halfway", t => t
                .Channel("email", c => c.Subject("tr", "Konu var")))).Message,
            StringComparison.Ordinal);

        options.AddTemplate("ok", t => t.Channel("email", c => c.Body("", "b")));
        Assert.Contains("already registered",
            Assert.Throws<InvalidOperationException>(() => options.AddTemplate("ok", t => t.Channel("email", c => c.Body("", "b")))).Message,
            StringComparison.Ordinal);
        Assert.Contains("ok", Assert.Throws<InvalidOperationException>(() => options.Template("nope")).Message, StringComparison.Ordinal);
        Assert.Contains("no 'sms' channel",
            Assert.Throws<InvalidOperationException>(() => options.Template("ok").ChannelTemplate("sms")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unclosed_or_unknown_token_shapes_pass_through_or_refuse_predictably()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("odd", t => t.Channel("email", c => c.Body("", "brace {{ not closed")));
        var (_, body, _) = options.Template("odd").ChannelTemplate("email").Render("", new Dictionary<string, string>());
        Assert.Equal("brace {{ not closed", body);   // an unclosed brace is literal text, not a crash
    }

    [Fact]
    public void Spaced_tokens_render_and_builder_order_is_free()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("spaced", t => t.Channel("email", c => c
            .Body("", "Hello {{ Name }}")        // spaces inside the braces are noise
            .Subject("", "Hi {{Name}}")));       // subject registered AFTER body — both must survive
        var (subject, body, _) = options.Template("spaced").ChannelTemplate("email")
            .Render("", new Dictionary<string, string> { ["Name"] = "Ömer" });
        Assert.Equal("Hi Ömer", subject);
        Assert.Equal("Hello Ömer", body);
    }

    [Fact]
    public void A_template_with_no_invariant_fallback_refuses_foreign_cultures()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("tr-only", t => t.Channel("email", c => c.Body("tr", "Sadece Türkçe")));
        var e = Assert.Throws<InvalidOperationException>(() =>
            options.Template("tr-only").ChannelTemplate("email").Render("de-DE", new Dictionary<string, string>()));
        Assert.Contains("no invariant fallback", e.Message, StringComparison.Ordinal);
        Assert.Contains("de-DE", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_token_render_is_exact_including_edges()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("multi", t => t.Channel("email", c => c.Body("", "{{A}} orta {{B}} son")));
        var (_, body, _) = options.Template("multi").ChannelTemplate("email")
            .Render("", new Dictionary<string, string> { ["A"] = "baş", ["B"] = "iki" });
        Assert.Equal("baş orta iki son", body);   // token at position 0 + trailing text — exact
    }

    [Fact]
    public void Teaching_messages_list_registrations_comma_separated()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("first", t => t.Channel("email", c => c.Body("", "a")).Channel("webhook", c => c.Body("", "b")));
        options.AddTemplate("second", t => t.Channel("email", c => c.Body("", "c")));

        Assert.Contains("email, webhook",
            Assert.Throws<InvalidOperationException>(() => options.Template("first").ChannelTemplate("sms")).Message,
            StringComparison.Ordinal);
        Assert.Contains("first, second",
            Assert.Throws<InvalidOperationException>(() => options.Template("missing")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_dedup_key_is_not_optional()
    {
        var e = Assert.Throws<ArgumentException>(() => new GoldpathNotificationRequest(
            "welcome", "email", "a@b.c", "tr", new Dictionary<string, string>(), dedupKey: "  "));
        Assert.Contains("dedup key", e.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Threat model T-NTF-03: a token's VALUE is inserted as text and never rendered again, so a
    /// value an end user controls (a name typed into a form) cannot pull another token's value
    /// into the notice.
    /// </summary>
    [Fact]
    public void A_token_value_is_inserted_as_text_never_rendered_again()
    {
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("otp", t => t.Channel("email", c => c
            .Subject("", "Hello {{Name}}")
            .Body("", "Dear {{Name}}, your code is {{Code}}.")));

        var (subject, body, _) = options.Template("otp").ChannelTemplate("email")
            .Render("", new Dictionary<string, string> { ["Name"] = "{{Code}}", ["Code"] = "481516" });

        Assert.Equal("Hello {{Code}}", subject);
        Assert.Equal("Dear {{Code}}, your code is 481516.", body);
    }
}
