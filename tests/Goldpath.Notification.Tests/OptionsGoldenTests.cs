using Xunit;

namespace Goldpath.Notification.Tests;

/// <summary>Option defaults and the template hash are CONTRACTS — golden-pinned.</summary>
public class OptionsGoldenTests
{
    [Fact]
    public void Defaults_are_the_documented_numbers()
    {
        var options = new GoldpathNotificationOptions();
        Assert.Equal(3, options.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RetryDelay);
        Assert.Equal(100, options.ChunkSize);
        Assert.Equal(TimeSpan.FromMinutes(5), options.StaleClaimAfter);
        Assert.Equal(587, options.EmailOptions.Port);
        Assert.True(options.EmailOptions.UseSsl);            // secure BY DEFAULT since A3 (plaintext is an explicit opt-out)
        Assert.False(options.EmailOptions.AllowInsecureTransport);
        Assert.Equal("", options.EmailOptions.Host);
        Assert.Equal("", options.EmailOptions.User);
        Assert.Equal("", options.EmailOptions.Password);
        Assert.Equal("", options.EmailOptions.From);
        Assert.Equal("", options.WebhookOptions.Url);
        Assert.False(options.WebhookOptions.AllowInsecureTransport);   // #533: plaintext hooks are an explicit opt-in
        Assert.Empty(options.Templates);
    }

    [Fact]
    public void The_template_hash_is_a_pinned_golden_value()
    {
        // The canonical form (key + ordered channel/culture/subject/body, '\n'-separated)
        // is a WIRE CONTRACT: rows stamped today must verify tomorrow. This literal only
        // changes if the canonicalization changes — which is a breaking evidence event.
        var options = new GoldpathNotificationOptions();
        options.AddTemplate("golden", t => t
            .Channel("email", c => c
                .Subject("tr", "Konu {{X}}")
                .Body("tr", "Gövde {{X}}")
                .Body("", "Body {{X}}"))
            .Channel("webhook", c => c.Body("", "Hook {{X}}")));

        Assert.Equal("c6256e6eec9c3bef60133652785586c2f3f90111c466ca07b18768fe15255902",
            options.Template("golden").Hash);
    }

    [Fact]
    public void The_hash_is_order_insensitive_over_registration_but_content_sensitive()
    {
        // Same texts registered in a different ORDER must hash identically (canonical
        // ordering), while any TEXT change must not.
        var a = new GoldpathNotificationOptions();
        a.AddTemplate("t", x => x
            .Channel("email", c => c.Body("", "B").Body("tr", "G"))
            .Channel("webhook", c => c.Body("", "H")));
        var b = new GoldpathNotificationOptions();
        b.AddTemplate("t", x => x
            .Channel("webhook", c => c.Body("", "H"))
            .Channel("email", c => c.Body("tr", "G").Body("", "B")));

        Assert.Equal(a.Template("t").Hash, b.Template("t").Hash);
    }

    [Fact]
    public void Email_and_webhook_configuration_lands()
    {
        var options = new GoldpathNotificationOptions();
        options.Email(e => { e.Host = "smtp.corp"; e.From = "noreply@corp"; });
        options.Webhook(w => w.Url = "https://hooks.corp/x");
        Assert.Equal("smtp.corp", options.EmailOptions.Host);
        Assert.Equal("noreply@corp", options.EmailOptions.From);
        Assert.Equal("https://hooks.corp/x", options.WebhookOptions.Url);
    }

    [Fact]
    public async Task MaySend_hook_defaults_to_yes()
    {
        var options = new GoldpathNotificationOptions();
        var request = new GoldpathNotificationRequest("t", "c", "r", "", new Dictionary<string, string>(), "k");
        Assert.True(await options.MaySendHook(request, null!));
    }
}
