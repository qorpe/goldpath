using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Goldpath.Jobs.TestHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The read role across every admin surface (admin-contract revision R4.1, slice R4-S1, #297): the seven module
/// surfaces and the served console, mapped on ONE host the way an adopter maps them, behind Goldpath.Auth's
/// policies. The route table holds every route to the method rule — a <c>GET</c> demands
/// <see cref="GoldpathPolicies.OpsRead"/> and nothing more, every other method carries the operator's verb floor —
/// so a route added later cannot ship in neither bucket. Over real HTTP on PostgreSQL, a <c>goldpath-ops-read</c>
/// identity reads every surface and is refused every verb in words; a <c>goldpath-ops</c> identity does both; no
/// identity is challenged; and a viewer's archive retrieval returns the archived document, as R4.1 decided.
/// </summary>
[Collection("quartz-process-globals")]
public sealed class AdminReadRoleSurfaceTests : IAsyncLifetime
{
    private const string RolesHeader = "X-Test-Roles";
    private const string ArchivedDocument = "{\"id\":\"P-1\",\"holder\":\"A. Holder\"}";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connection = _postgres.GetConnectionString();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["ConnectionStrings:jobsdb"] = connection;
        builder.Services.AddDbContext<ClusterDb>(o => o.UseNpgsql(connection));

        // Goldpath.Auth's policies, exactly as an adopter registers them; the identity comes from a header, so one
        // host can be read as a viewer, an operator and nobody.
        builder.AddGoldpathAuth(auth => auth.Strategy = GoldpathAuthStrategy.None);
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderRolesHandler>(HeaderRolesHandler.Name, _ => { });
        builder.Services.Configure<AuthenticationOptions>(o => o.DefaultScheme = HeaderRolesHandler.Name);

        builder.Services.AddScoped<IGoldpathBulkRowHandler<ClusterPaymentRow>, SmokePaymentHandler>();
        builder.AddGoldpathBulk<WebApplicationBuilder, ClusterDb>(bulk =>
            bulk.AddBatch<ClusterPaymentRow>("payments", b => b.MaxRows(1_000).RowKey(r => r.EndToEndId)));
        builder.Services.AddScoped<IGoldpathCampaignItemHandler<SmokeCustomer>, SmokeCampaignHandler>();
        builder.AddGoldpathCampaign<WebApplicationBuilder, ClusterDb>(campaign =>
            campaign.AddCampaign<SmokeCustomer>("welcome", c => c
                .MaxTargets(1_000)
                .Targets((services, _) => services.GetRequiredService<ClusterDb>()
                    .CampaignCustomers.AsNoTracking().OrderBy(x => x.Id).Select(x => new SmokeCustomer(x.Id, x.Email)).AsAsyncEnumerable())));
        builder.AddGoldpathFileExchange<WebApplicationBuilder, ClusterDb>(files => files
            .AddRail<SmokeRegistryRow>("registry-daily", rail => rail
                .Header(1)
                .ParseLine(line => new SmokeRegistryRow(line, 1m))
                .Handle((_, _) => Task.CompletedTask)));
        builder.AddGoldpathApprovals<WebApplicationBuilder, ClusterDb>(approvals => approvals
            .AddLadder("credit-limit", ladder => ladder.TopRung("manager", TimeSpan.FromHours(8))));
        builder.AddGoldpathDataProtection();
        builder.AddGoldpathArchival<WebApplicationBuilder, ClusterDb>(archival =>
            archival.AddArchive<SmokePolicy>(a => a
                .Named("policies")
                .Key(x => x.Id)
                .DueWhen(x => x.ClosedAt != null, x => x.ClosedAt!.Value)
                .ArchiveAfter(TimeSpan.Zero)
                .RetainFor(years: 10)));
        builder.AddGoldpathNotification<WebApplicationBuilder, ClusterDb>(notification =>
        {
            notification.Webhook(w => w.Url = "http://127.0.0.1:9/hook");
            notification.AddTemplate("welcome", t => t.Channel("webhook", c => c.Body("", "Welcome.")));
        });
        builder.AddGoldpathJobsManagement<WebApplicationBuilder, ClusterDb>(jobs => jobs.ConnectionName = "jobsdb");

        _app = builder.Build();
        _app.UseGoldpathAuth();
        _app.MapGoldpathJobsAdmin<ClusterDb>();
        _app.MapGoldpathBulkAdmin<ClusterDb>();
        _app.MapGoldpathNotificationAdmin<ClusterDb>();
        _app.MapGoldpathArchivalAdmin<ClusterDb>();
        _app.MapGoldpathFileExchangeAdmin();
        _app.MapGoldpathApprovalsAdmin();
        _app.MapGoldpathCampaignAdmin<ClusterDb>();
        _app.MapGoldpathConsole(configure: console => console.AddService("orders", "https://orders.internal/goldpath/admin"));   // a registry, so its config answers

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ClusterDb>();
            await db.Database.EnsureCreatedAsync();
            db.Set<GoldpathArchiveEntry>().Add(new GoldpathArchiveEntry
            {
                Definition = "policies",
                AggregateKey = "P-1",
                Document = ArchivedDocument,
                SchemaVersion = 1,
                DueAt = DateTimeOffset.UtcNow.AddDays(-1),
                ArchivedAt = DateTimeOffset.UtcNow,
                ChainIndex = 0,
                ContentHash = "content",
                ChainHash = "chain",
                PreviousHash = "",
            });
            await db.SaveChangesAsync();
        }

        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        QuartzProcessGlobals.Pin();
        await _postgres.DisposeAsync();
    }

    private List<(RouteEndpoint Endpoint, string Pattern, IReadOnlyList<string> Methods)> AdminRoutes()
        => [.. ((IEndpointRouteBuilder)_app).DataSources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/goldpath/", StringComparison.Ordinal) == true)
            .Select(e => (e, e.RoutePattern.RawText!, (IReadOnlyList<string>)(e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])))];

    private static bool IsRead(IReadOnlyList<string> methods)
        => methods.Count > 0 && methods.All(m => HttpMethods.IsGet(m) || HttpMethods.IsHead(m));

    [Fact]
    public void Every_admin_route_is_a_read_for_the_viewer_or_a_verb_for_the_operator()
    {
        var routes = AdminRoutes();

        // All eight mounts are in the table, so a surface that stops mapping cannot pass by absence.
        var mounts = routes.Select(r => string.Join('/', r.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(3))).Distinct().ToList();
        foreach (var mount in new[] { "goldpath/admin/jobs", "goldpath/admin/bulk", "goldpath/admin/notification", "goldpath/admin/archival",
                     "goldpath/admin/fileexchange", "goldpath/admin/approvals", "goldpath/admin/campaign", "goldpath/console" })
        {
            Assert.Contains(mounts, m => m.StartsWith(mount, StringComparison.Ordinal));
        }

        Assert.All(routes, route =>
        {
            var policies = route.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();
            Assert.True(policies.SequenceEqual([GoldpathPolicies.OpsRead]), $"{string.Join(",", route.Methods)} {route.Pattern} demands [{string.Join(", ", policies)}]");
            var verbFloor = route.Endpoint.Metadata.GetMetadata<AdminVerbFloor>() is not null;
            Assert.True(IsRead(route.Methods) != verbFloor,
                $"{string.Join(",", route.Methods)} {route.Pattern}: a read must not carry the verb floor and a verb must");
        });
        Assert.Contains(routes, r => !IsRead(r.Methods));
    }

    [Fact]
    public async Task A_viewer_reads_every_surface_and_every_verb_is_refused_in_words()
    {
        var viewer = Client("goldpath-ops-read");
        var routes = AdminRoutes();

        foreach (var (_, pattern, _) in routes.Where(r => IsRead(r.Methods) && !r.Pattern.Contains('{', StringComparison.Ordinal)))
        {
            using var read = await viewer.GetAsync(pattern);
            Assert.True(read.StatusCode == HttpStatusCode.OK, $"GET {pattern} answered {(int)read.StatusCode} to a viewer");
        }

        foreach (var (_, pattern, methods) in routes.Where(r => !IsRead(r.Methods)))
        {
            using var refused = await viewer.SendAsync(Verb(methods[0], pattern));
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{methods[0]} {pattern} answered {(int)refused.StatusCode} to a viewer");
            using var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("this verb needs the goldpath-ops role; this identity can read this surface but not act on it",
                envelope.RootElement.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task A_viewer_retrieves_an_archived_document_in_full()
    {
        // R4.1's disclosure, pinned: archive retrieval is a GET, so the read role returns the document as stored.
        using var entry = await Client("goldpath-ops-read").GetAsync("/goldpath/admin/archival/entries/policies/P-1");
        Assert.Equal(HttpStatusCode.OK, entry.StatusCode);
        using var body = JsonDocument.Parse(await entry.Content.ReadAsStringAsync());
        Assert.Equal(ArchivedDocument, body.RootElement.GetProperty("document").GetString());
    }

    [Fact]
    public async Task An_operator_reads_and_acts_and_nobody_is_challenged()
    {
        var operatorClient = Client("goldpath-ops");
        var anonymous = _app.GetTestClient();
        var routes = AdminRoutes();

        foreach (var (_, pattern, _) in routes.Where(r => IsRead(r.Methods) && !r.Pattern.Contains('{', StringComparison.Ordinal)))
        {
            using var read = await operatorClient.GetAsync(pattern);
            Assert.True(read.StatusCode == HttpStatusCode.OK, $"GET {pattern} answered {(int)read.StatusCode} to an operator");
            using var challenged = await anonymous.GetAsync(pattern);
            Assert.True(challenged.StatusCode == HttpStatusCode.Unauthorized, $"GET {pattern} answered {(int)challenged.StatusCode} to nobody");
        }

        foreach (var (_, pattern, methods) in routes.Where(r => !IsRead(r.Methods)))
        {
            // The handler answers (an unknown id, a body it refuses): what matters is that the role let it. An empty
            // body can make a handler throw (the in-process server rethrows it): that is the handler running too.
            try
            {
                using var acted = await operatorClient.SendAsync(Verb(methods[0], pattern));
                Assert.True(acted.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                    $"{methods[0]} {pattern} answered {(int)acted.StatusCode} to an operator");
            }
            catch (Exception thrown) when (thrown is not Xunit.Sdk.XunitException)
            {
                // Reached the handler, past the role check: nothing in the floor throws.
            }

            using var challenged = await anonymous.SendAsync(Verb(methods[0], pattern));
            Assert.True(challenged.StatusCode == HttpStatusCode.Unauthorized, $"{methods[0]} {pattern} answered {(int)challenged.StatusCode} to nobody");
        }
    }

    private HttpClient Client(string role)
    {
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Add(RolesHeader, role);
        return client;
    }

    // Route values filled with a value of the right shape (a guid where the route constrains one), and an empty JSON
    // object for a body: enough to reach the role check, which runs after binding and before any handler.
    private static HttpRequestMessage Verb(string method, string pattern)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{([^}]*)\}",
            m => m.Groups[1].Value.Contains(":guid", StringComparison.Ordinal) ? Guid.NewGuid().ToString() : "x");
        return new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
    }

    /// <summary>An identity from a header: the roles listed, or no identity at all when the header is absent.</summary>
    private sealed class HeaderRolesHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "header-roles";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(RolesHeader, out var roles))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-user"), .. roles.ToString().Split(',').Select(r => new Claim(ClaimTypes.Role, r.Trim()))], Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
        }
    }
}
