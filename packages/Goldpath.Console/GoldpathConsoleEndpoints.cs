using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Goldpath;

/// <summary>One service the console can drive: the operator's word for it, and its head.</summary>
public sealed record GoldpathConsoleService(string Name, string AdminBaseUrl);

/// <summary>
/// A product console the core console LINKS to and never probes (console RFC §3, link
/// tiles; decision record <c>docs/rfc/goldpath-console-link-tiles.md</c>): the operator's
/// word for it, and its address. A tile asserts an address, never health — whether the
/// product is well is shown on its own console, behind its own floor.
/// </summary>
public sealed record GoldpathConsoleLink(string Name, string Url);

/// <summary>
/// How the console is served. The registry lives HERE rather than in a file next to the
/// dist: an adopter who already declares their services in configuration should not also
/// have to drop JSON into a static folder.
/// </summary>
public sealed class GoldpathConsoleOptions
{
    /// <summary>The cross-service registry. Empty means "this app only" — the common case.</summary>
    public IList<GoldpathConsoleService> Services { get; } = [];

    /// <summary>
    /// Product consoles shown as link tiles on Today. Never probed: a link carries no
    /// capability discovery, needs no CORS and never becomes a triage row.
    /// </summary>
    public IList<GoldpathConsoleLink> Links { get; } = [];

    /// <summary>Adds a service the console should offer (console RFC §3).</summary>
    public GoldpathConsoleOptions AddService(string name, string adminBaseUrl)
    {
        Services.Add(new GoldpathConsoleService(name, adminBaseUrl));
        return this;
    }

    /// <summary>
    /// Adds a link tile to a product's own console. <paramref name="url"/> is a root-relative
    /// path (<c>/coexist/console/</c>) or an absolute http(s) address; anything else is
    /// refused HERE, so the stack trace points at the line that wrote it, and
    /// <c>MapGoldpathConsole</c> refuses the whole list again at startup.
    /// </summary>
    public GoldpathConsoleOptions AddLink(string name, string url)
    {
        var link = new GoldpathConsoleLink(name, url);
        ConsoleLinkRules.Validate([.. Links, link]);
        Links.Add(link);
        return this;
    }
}

/// <summary>
/// The ONE rule for a link the console will draw (ADR-0005: it ships with its verifier).
/// Run eagerly by <see cref="GoldpathConsoleOptions.AddLink"/> and again over the whole list
/// by <c>MapGoldpathConsole</c>, so a <c>Links.Add(new(...))</c> that bypassed the builder is
/// still refused before the app serves a single request.
/// </summary>
internal static class ConsoleLinkRules
{
    private const string Shape =
        "a link is a root-relative path such as /coexist/console/ or an absolute http(s) address such as https://mockifyr.internal/ (docs/rfc/goldpath-console-link-tiles.md §5)";

    /// <summary>Refuses the first link that breaks the rule, naming it — never echoing its URL.</summary>
    internal static void Validate(IEnumerable<GoldpathConsoleLink> links)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (string.IsNullOrWhiteSpace(link.Name))
            {
                throw new InvalidOperationException(
                    "A console link has no name. The tile shows the operator's word for the product — give it one.");
            }

            // Two tiles with the same name cannot be told apart by the operator who has to pick one.
            if (!names.Add(link.Name.Trim()))
            {
                throw new InvalidOperationException(
                    $"The console link '{link.Name}' is configured twice. Two identical tiles cannot be told apart — name each product once.");
            }

            if (RefusalOf(link.Url) is { } reason)
            {
                // The URL itself is NOT echoed: a refused one may carry credentials, and this
                // message lands in a startup log.
                throw new InvalidOperationException($"The console link '{link.Name}' was refused: {reason}; {Shape}.");
            }
        }
    }

    /// <summary>Why <paramref name="url"/> is not a link the console will draw, or null when it is.</summary>
    internal static string? RefusalOf(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "its address is empty";
        }

        // Whitespace and control characters have no place in an address a browser is sent to,
        // and browsers STRIP some of them ("java<TAB>script:") — refusing them closes that door.
        if (url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "its address contains whitespace or a control character";
        }

        // A browser reads "\" as "/" in an http(s) URL, so "/\host" is "//host": another origin.
        if (url.Contains('\\'))
        {
            return "its address contains a backslash";
        }

        // ORDER MATTERS. On Linux and macOS, Uri.TryCreate("/coexist/console/", Absolute)
        // SUCCEEDS as file:///coexist/console/ — so the root-relative form must be decided
        // before the absolute parse ever sees it.
        if (url[0] == '/')
        {
            return url.Length > 1 && url[1] == '/'
                ? "its address is protocol-relative (//host), which names another origin without saying which scheme"
                : null;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "its address is neither root-relative nor an absolute http(s) address";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var absolute) || absolute.Host.Length == 0)
        {
            return "its address is not a well-formed absolute URL";
        }

        // A credential never rides a link: it would sit in every operator's browser history.
        return absolute.UserInfo.Length > 0 ? "its address carries credentials (user:password@)" : null;
    }
}

/// <summary>
/// Serves the Goldpath console from the app's own management head (console RFC D1).
/// <para>
/// The console is a single page that talks to the FROZEN admin contract with the
/// operator's own credentials — it adds no capability the API does not already expose.
/// Its assets are embedded in this package: adopters never run Node, and a generated app
/// stays Node-free by construction.
/// </para>
/// </summary>
public static class GoldpathConsoleEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Maps the console under <paramref name="prefix"/>, behind the SAME ops floor as the
    /// admin surfaces (H2). `exposeUnsecured: true` is for hosts that have no auth at all
    /// — the guard logs the choice, exactly as the admin mappers do.
    /// </summary>
    public static IEndpointRouteBuilder MapGoldpathConsole(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/goldpath/console",
        bool exposeUnsecured = false,
        Action<GoldpathConsoleOptions>? configure = null)
    {
        var options = new GoldpathConsoleOptions();
        configure?.Invoke(options);
        // Again, over the WHOLE list: AddLink refused eagerly, but Links is a plain list and a
        // Links.Add(new(...)) never met that check. A bad link stops the app at startup, not
        // at the first operator's click. The validated list is what the endpoint serves.
        ConsoleLinkRules.Validate(options.Links);
        var links = options.Links.ToArray();

        var assembly = typeof(GoldpathConsoleEndpoints).Assembly;
        // The resource prefix follows the ROOT NAMESPACE (Goldpath), not the assembly name —
        // `Goldpath.Console.wwwroot` would find nothing and every request would 404.
        var files = new EmbeddedFileProvider(assembly, "Goldpath.wwwroot");
        var group = endpoints.MapGroup(prefix);
        AdminSurfaceGuard.Apply(endpoints, group, prefix, exposeUnsecured);

        // The registry, as the console reads it. Served from CONFIG so the same dist works
        // for every adopter.
        //
        // An app that configures NO service has no registry, and says so with 404 — which
        // the console reads as "this service only", silently and correctly. Answering with
        // an empty LIST instead looked equivalent and was not: the console reads an empty
        // registry as a BROKEN one and warns the operator that a service they configured is
        // missing. Every single-app adopter met that false warning on their first screen
        // (caught by the README screenshot run, because the two halves were each tested
        // alone and the seam between them never was).
        //
        // The same seam decides the links-only shape: `services` is OMITTED, never emptied.
        // An app with product links and no other service is still "this service only", and
        // an empty list would put the same false warning back on its first screen.
        group.MapGet("/console.config.json", () =>
        {
            if (options.Services.Count == 0 && links.Length == 0)
            {
                return Results.NotFound();
            }

            var registry = new Dictionary<string, object>();
            if (options.Services.Count > 0)
            {
                registry["services"] = options.Services.Select(service => new { name = service.Name, adminBaseUrl = service.AdminBaseUrl }).ToArray();
            }

            if (links.Length > 0)
            {
                registry["links"] = links.Select(link => new { name = link.Name, url = link.Url }).ToArray();
            }

            return Results.Content(JsonSerializer.Serialize(registry), "application/json", Encoding.UTF8);
        });

        group.MapGet("/{**path}", (string? path, HttpContext http) =>
        {
            // Off by a slash: at "/goldpath/console" (no trailing slash) the page would
            // serve, but its RELATIVE asset urls would resolve one directory too high —
            // a blank screen with a green status code. Redirect to the canonical root.
            if (string.IsNullOrEmpty(path) && http.Request.Path.Value is { } raw && !raw.EndsWith('/'))
            {
                return Results.Redirect(raw + "/" + http.Request.QueryString);
            }

            return Serve(files, path);
        });

        return endpoints;
    }

    /// <summary>
    /// Resolves one request against the embedded console. Internal rather than private so
    /// the three outcomes an operator can hit — the page, a missing asset, and a package
    /// built without a console — are proven deterministically instead of depending on
    /// whether the machine running the tests happened to build the dist.
    /// </summary>
    internal static IResult Serve(IFileProvider files, string? path)
    {
        var requested = path?.Replace('\\', '/').Trim('/') ?? "";

        // The PAGE is the prefix root, and only the root. The console has no client-side
        // routes yet (sections are state, not URLs), so there is nothing to fall back TO:
        // serving the page at an arbitrary path would answer 200 with a document whose
        // relative asset URLs resolve one directory too deep — a blank screen with a green
        // status code. When real routes arrive, this is the place that must also inject a
        // <base href> (open-threads T9).
        if (requested.Length == 0)
        {
            var page = files.GetFileInfo("index.html");
            return page.Exists
                ? Results.Stream(page.CreateReadStream(), ContentTypeOf("index.html"))
                : Results.Problem(
                    "This build of Goldpath.Console carries no console. The package is built by Goldpath's CI from ui/console; a locally built one needs scripts/build-console.sh first.",
                    statusCode: StatusCodes.Status500InternalServerError);
        }

        var asset = files.GetFileInfo(requested);
        // A missing asset must 404 rather than answer with HTML the browser cannot parse —
        // that is how a console "loads" into a blank screen with nothing in the log.
        return asset.Exists ? Results.Stream(asset.CreateReadStream(), ContentTypeOf(requested)) : Results.NotFound();
    }

    /// <summary>
    /// The framework's own extension→type map (ADR-0003: compose, never rewrite). Anything
    /// it does not know is served as bytes rather than guessed at — a guessed content type
    /// is how a console ends up rendered as text.
    /// </summary>
    private static string ContentTypeOf(string path)
        => ContentTypes.TryGetContentType(path, out var contentType) ? contentType : "application/octet-stream";

}
