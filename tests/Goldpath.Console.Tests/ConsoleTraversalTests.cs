using System.Net;
using Goldpath;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Goldpath.Console.Tests;

/// <summary>
/// Threat model T-CON-03: an asset path must never walk out of the console's bundle. Today
/// the bundle is an embedded provider, which can only return resources compiled into the
/// assembly, so these tests are green by construction. They exist for the day somebody
/// swaps <see cref="GoldpathConsoleEndpoints.ConsoleFiles"/> for a disk-backed provider (a
/// dev-time hot-reload path, say): every traversal below then names a file that EXISTS on
/// the machine running the tests, so a provider that follows the path answers 200 with that
/// file's bytes and the test goes red instead of the route quietly becoming a file read.
/// </summary>
public class ConsoleTraversalTests
{
    /// <summary>
    /// A real file ABOVE the test's content root: this test project's own csproj. Found by
    /// walking up from the output directory rather than by a fixed depth, so the target still
    /// exists if the output layout changes; the climb is how many <c>..</c> the payloads need.
    /// </summary>
    private static readonly (int Depth, string FullPath) Target = FindTarget();

    private static string Climb(string segment)
        => string.Concat(Enumerable.Repeat(segment, Target.Depth)) + "Goldpath.Console.Tests.csproj";

    /// <summary>
    /// Every traversal shape the issue names, each aimed at a file that exists. The comment
    /// on each is what the route value looks like when the shape arrives over HTTP — measured,
    /// not assumed: the catch-all keeps an encoded <c>%2F</c> encoded, decodes <c>%5C</c>,
    /// and the console's own normalisation then turns every <c>\</c> into <c>/</c>.
    /// </summary>
    public static TheoryData<string, string> Traversals() => new()
    {
        // ../ — a raw client (no dot-segment normalisation) delivers it as is.
        { "dot-dot slash", Climb("../") },
        // ..\ — the console rewrites \ to / before resolving, so this IS ../ by the time a
        // provider sees it.
        { "dot-dot backslash", Climb("..\\") },
        // ..%5c — decoded to ..\ by routing, so it reaches the provider as ../ too: the
        // encoded shape that survives every client.
        { "encoded backslash", Climb("..%5c") },
        // %2e%2e%2f and ..%2f — routing keeps %2F encoded in a catch-all, so these arrive as
        // the literal text "..%2f". Pinned so a routing change that decoded them would show.
        { "encoded dot-dot slash", Climb("%2e%2e%2f") },
        { "encoded slash", Climb("..%2f") },
        // An absolute path to the same real file, and the classic one.
        { "absolute", Target.FullPath },
        { "absolute /etc/passwd", "/etc/passwd" },
        // Drive letters: rooted on Windows, where Path.Combine would discard the root.
        { "drive letter", "C:/Windows/win.ini" },
        { "drive letter backslash", "C:\\Windows\\win.ini" },
        // The issue's own two examples, verbatim.
        { "the issue's ../appsettings.json", "../appsettings.json" },
        { "the issue's ..%2f..%2fweb.config", "..%2f..%2fweb.config" },
    };

    [Theory]
    [MemberData(nameof(Traversals))]
    public void Serve_answers_404_for_a_traversal_against_the_provider_the_route_uses(string shape, string path)
    {
        // The route value as routing hands it over: %5c decoded, %2f kept (see Traversals).
        var routeValue = path.Replace("%5c", "\\", StringComparison.OrdinalIgnoreCase);

        var result = GoldpathConsoleEndpoints.Serve(GoldpathConsoleEndpoints.ConsoleFiles(), routeValue);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        Assert.True(status == StatusCodes.Status404NotFound, $"{shape}: '{routeValue}' answered {status}, not 404");
    }

    [Theory]
    [MemberData(nameof(Traversals))]
    public async Task The_mapped_route_answers_404_for_a_traversal_from_a_raw_client(string shape, string path)
    {
        // A RAW request: the path is set on the server side exactly as written, so no client
        // collapses the dot segments first (HttpClient would turn /console/../x into /x and the
        // request would never reach the console's route at all — a 404 proving nothing).
        await using var app = await HostAsync();
        var context = await app.GetTestServer().SendAsync(request =>
        {
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = "/goldpath/console/" + path.TrimStart('/');
        });

        Assert.True(
            context.Response.StatusCode == StatusCodes.Status404NotFound,
            $"{shape}: answered {context.Response.StatusCode}, not 404");
    }

    [Theory]
    [MemberData(nameof(Traversals))]
    public async Task The_mapped_route_answers_404_for_a_traversal_from_an_ordinary_client(string shape, string path)
    {
        await using var app = await HostAsync();
        var client = app.GetTestClient();

        // Through HttpClient, as a browser or curl without --path-as-is would send it. The
        // absolute shape keeps its own leading slash, so the route value starts with one.
        var response = await client.GetAsync(new Uri("/goldpath/console/" + path, UriKind.Relative));

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"{shape}: {response.RequestMessage?.RequestUri?.AbsolutePath} answered {(int)response.StatusCode}, not 404");
    }

    [Fact]
    public void Every_traversal_aims_at_a_file_that_exists()
    {
        // The pin is only worth something if a provider that FOLLOWED the path would find a
        // file: a payload aimed at nothing stays 404 under any provider and proves nothing.
        Assert.True(File.Exists(Target.FullPath), Target.FullPath);
        var resolved = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Climb("../")));
        Assert.Equal(Target.FullPath, resolved);
    }

    private static async Task<WebApplication> HostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGoldpathConsole(exposeUnsecured: true);
        await app.StartAsync();
        return app;
    }

    private static (int Depth, string FullPath) FindTarget()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "Goldpath.Console.Tests.csproj");
            if (depth > 0 && File.Exists(candidate))
            {
                return (depth, candidate);
            }
        }

        throw new InvalidOperationException($"No Goldpath.Console.Tests.csproj above {AppContext.BaseDirectory}.");
    }
}
