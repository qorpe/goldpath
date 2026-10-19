using System.Net;
using System.Text.Json;
using Goldpath;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Goldpath.Console.Tests;

/// <summary>
/// Product console link tiles (#257; decision record docs/rfc/goldpath-console-link-tiles.md).
/// Two things are owned here: the URL rule (§5 — the standard ships with its verifier,
/// ADR-0005) and the registry document's shape (§4 — `services` OMITTED, never emptied, when
/// only links exist). Whether a tile renders and navigates is proven by the console smoke.
/// </summary>
public class ConsoleLinkTests
{
    private static async Task<HttpClient> HostAsync(Action<GoldpathConsoleOptions> configure, bool secured = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        if (secured)
        {
            builder.AddGoldpathAuth(auth => auth.Strategy = GoldpathAuthStrategy.None);
        }

        var app = builder.Build();
        if (secured)
        {
            app.UseGoldpathAuth();
        }

        app.MapGoldpathConsole(exposeUnsecured: !secured, configure: configure);
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static async Task<JsonElement> RegistryAsync(HttpClient client)
    {
        var response = await client.GetAsync("/goldpath/console/console.config.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    // ── §5 rule 2: the two forms a link may take ───────────────────────────────────────

    [Theory]
    [InlineData("/coexist/console/")]                 // the Coexist shape: same origin, its own prefix
    [InlineData("/")]                                 // the app's own root
    [InlineData("https://mockifyr.internal/")]       // the Mockifyr shape: another origin
    [InlineData("http://10.0.0.5:8080/ui/")]          // plain http: air-gapped ops networks are first-class
    [InlineData("HTTPS://Mockifyr.Internal/ui?x=1")]  // schemes are case-insensitive
    public void A_root_relative_path_or_an_absolute_http_address_is_a_link(string url)
    {
        var options = new GoldpathConsoleOptions().AddLink("Product", url);

        Assert.Equal(new GoldpathConsoleLink("Product", url), Assert.Single(options.Links));
    }

    // ── §5 rule 3: everything else is refused, eagerly, in AddLink ────────────────────

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("blob:https://mockifyr.internal/1")]
    [InlineData("//evil.example/")]                  // protocol-relative: another origin, unnamed scheme
    [InlineData("/\\evil.example/")]                 // a browser reads "/\" as "//"
    [InlineData("https://evil.example\\@good/")]     // backslash anywhere
    [InlineData("console/")]                         // path-relative: resolves differently per console prefix
    [InlineData("../x")]
    [InlineData("mockifyr.internal")]                // a bare host is a relative path to a browser
    [InlineData("https://u:p@mockifyr.internal/")]   // a credential never rides a link
    [InlineData("https://")]                         // no host
    [InlineData("https:mockifyr.internal")]          // no "//": not the absolute form
    [InlineData("java\tscript:alert(1)")]            // browsers STRIP the tab
    [InlineData(" /coexist/console/")]               // whitespace
    [InlineData("/coexist/\nconsole/")]              // control character
    [InlineData("")]
    public void Any_other_address_is_refused_at_the_line_that_wrote_it(string url)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new GoldpathConsoleOptions().AddLink("Product", url));

        // The message names the link and teaches the two accepted forms.
        Assert.Contains("'Product'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("root-relative path", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_never_echoes_the_address_because_it_may_carry_a_credential_into_a_log()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => new GoldpathConsoleOptions().AddLink("Mockifyr", "https://operator:s3cret@mockifyr.internal/"));

        Assert.DoesNotContain("s3cret", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("javascript:alert(1)", "neither root-relative nor an absolute http(s) address")]
    [InlineData("//evil.example/", "protocol-relative")]
    [InlineData("/\\evil.example/", "backslash")]
    [InlineData("https://u:p@mockifyr.internal/", "carries credentials")]
    [InlineData("https://", "not a well-formed absolute URL")]
    [InlineData("java\tscript:alert(1)", "whitespace or a control character")]
    [InlineData("", "empty")]
    public void Each_refusal_says_WHICH_rule_the_address_broke(string url, string reason)
    {
        Assert.Contains(reason, ConsoleLinkRules.RefusalOf(url) ?? "(accepted)", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_link_without_a_name_is_refused(string name)
    {
        var refusal = Assert.Throws<InvalidOperationException>(() => new GoldpathConsoleOptions().AddLink(name, "/coexist/console/"));

        Assert.Contains("has no name", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_links_with_the_same_name_are_refused_whatever_their_case()
    {
        var options = new GoldpathConsoleOptions().AddLink("Coexist", "/coexist/console/");

        var refusal = Assert.Throws<InvalidOperationException>(() => options.AddLink(" coexist ", "https://coexist.internal/"));

        Assert.Contains("configured twice", refusal.Message, StringComparison.Ordinal);
        Assert.Single(options.Links);   // the refused link was not half-added
    }

    // ── §5 rule 4: the order of checks ───────────────────────────────────────────────

    [Fact]
    public void The_root_relative_check_runs_BEFORE_the_absolute_parse_because_unix_reads_a_path_as_a_file_URI()
    {
        // The quirk itself, pinned on the OS the suite runs on: on Linux and macOS this
        // succeeds as file:///coexist/console/. If the absolute parse ran first, the Coexist
        // shape would be refused as a "file:" link on exactly the machines that ship it.
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(Uri.TryCreate("/coexist/console/", UriKind.Absolute, out var parsed));
            Assert.True(parsed!.IsFile);
        }

        Assert.Null(ConsoleLinkRules.RefusalOf("/coexist/console/"));
    }

    // ── §5: validated again at startup ───────────────────────────────────────────────

    [Fact]
    public async Task A_link_that_bypassed_AddLink_is_still_refused_at_MapGoldpathConsole()
    {
        // Links is a plain list: Add(new(...)) never met AddLink's check. The app must not
        // start serving it — the refusal is at startup, not at an operator's click.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HostAsync(console => console.Links.Add(new GoldpathConsoleLink("Evil", "javascript:alert(1)"))));

        Assert.Contains("'Evil'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_duplicate_that_bypassed_AddLink_is_still_refused_at_MapGoldpathConsole()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => HostAsync(console =>
        {
            console.AddLink("Coexist", "/coexist/console/");
            console.Links.Add(new GoldpathConsoleLink("COEXIST", "/other/"));
        }));

        Assert.Contains("configured twice", refusal.Message, StringComparison.Ordinal);
    }

    // ── §4: the registry document ────────────────────────────────────────────────────

    [Fact]
    public async Task Links_are_served_beside_services_in_the_order_they_were_written()
    {
        var client = await HostAsync(console => console
            .AddService("payments", "https://payments.internal")
            .AddLink("Coexist", "/coexist/console/")
            .AddLink("Mockifyr", "https://mockifyr.internal/"));

        var registry = await RegistryAsync(client);

        var service = Assert.Single(registry.GetProperty("services").EnumerateArray());
        Assert.Equal("payments", service.GetProperty("name").GetString());
        Assert.Collection(
            registry.GetProperty("links").EnumerateArray(),
            first => Assert.Equal(("Coexist", "/coexist/console/"), (first.GetProperty("name").GetString(), first.GetProperty("url").GetString())),
            second => Assert.Equal(("Mockifyr", "https://mockifyr.internal/"), (second.GetProperty("name").GetString(), second.GetProperty("url").GetString())));
    }

    [Fact]
    public async Task A_links_only_registry_OMITS_services_rather_than_sending_an_empty_list()
    {
        var client = await HostAsync(console => console.AddLink("Coexist", "/coexist/console/"));

        var registry = await RegistryAsync(client);

        // Read as JSON, not bound to a type: a typed bind would read an absent list and an
        // empty one identically, and the console does NOT — `services: []` is a BROKEN
        // registry to registry.ts, which puts a false warning on the operator's first screen.
        Assert.False(registry.TryGetProperty("services", out _), "a links-only registry must not carry a services key");
        Assert.Single(registry.GetProperty("links").EnumerateArray());
    }

    [Fact]
    public async Task A_services_only_registry_carries_no_links_key()
    {
        var client = await HostAsync(console => console.AddService("payments", "https://payments.internal"));

        var registry = await RegistryAsync(client);

        // The shipped shape is unchanged for every adopter who configures no link.
        Assert.False(registry.TryGetProperty("links", out _));
    }

    [Fact]
    public async Task Link_addresses_sit_behind_the_ops_floor()
    {
        // Link addresses are often internal hostnames: an anonymous request must never read them.
        var client = await HostAsync(console => console.AddLink("Mockifyr", "https://mockifyr.internal/"), secured: true);

        var response = await client.GetAsync("/goldpath/console/console.config.json");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
