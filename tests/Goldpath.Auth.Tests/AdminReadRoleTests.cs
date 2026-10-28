using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// The read role (admin-contract revision R4.1, #297), at the guard every admin mapper applies: the HTTP method
/// decides — a <c>GET</c> demands <see cref="GoldpathPolicies.OpsRead"/>, which <c>goldpath-ops-read</c> and
/// <c>goldpath-ops</c> both satisfy, and any other method also demands <see cref="GoldpathPolicies.Ops"/>, refused
/// with a 403 envelope that names the role. Real JWTs through Goldpath.Auth's own OpenID strategy. The surfaces
/// themselves are proven over one host in the integration suite (AdminReadRoleSurfaceTests).
/// </summary>
public sealed class AdminReadRoleTests
{
    private static async Task<WebApplication> StartAsync(Action<GoldpathAuthOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddGoldpathAuth(o =>
        {
            o.Audience = TestIdp.Audience;
            o.RequireHttpsMetadata = false;
            configure?.Invoke(o);
        });
        TestIdp.WireValidation(builder.Services);
        var app = builder.Build();
        app.UseGoldpathAuth();
        MapSurface(app);
        await app.StartAsync();
        return app;
    }

    // A surface the way every module maps one: a group, the guard, then a read and two verbs.
    private static void MapSurface(WebApplication app)
    {
        var group = app.MapGroup("/goldpath/admin/demo");
        AdminSurfaceGuard.Apply(app, group, "/goldpath/admin/demo", exposeUnsecured: false);
        group.MapGet("/things", () => Results.Ok(new { ok = true }));
        group.MapPost("/things/{id}/pause", (string id) => Results.Ok(new { ok = true, message = $"paused {id}" }));
        group.MapDelete("/things/{id}", (string id) => Results.Ok(new { ok = true, message = $"deleted {id}" }));

        // Routes no shipped surface maps but an adopter may: one that answers GET and POST, and one that names no method.
        group.MapMethods("/both", ["GET", "POST"], () => Results.Ok(new { ok = true }));
        group.Map("/any", () => Results.Ok(new { ok = true }));
    }

    private static HttpClient As(WebApplication app, string? role)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestIdp.Token(role: role));
        return client;
    }

    [Fact]
    public void Both_strategy_branches_register_the_read_policy_satisfied_by_the_read_role_or_the_ops_role()
    {
        foreach (var strategy in new[] { GoldpathAuthStrategy.None, GoldpathAuthStrategy.OpenId })
        {
            var builder = WebApplication.CreateBuilder();
            builder.AddGoldpathAuth(o =>
            {
                o.Strategy = strategy;
                o.OpsRole = "fleet-operators";
                o.OpsReadRole = "fleet-viewers";
            });
            using var app = builder.Build();
            var policy = app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.GetPolicy(GoldpathPolicies.OpsRead);

            Assert.NotNull(policy);
            var roles = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
            Assert.Equal(["fleet-viewers", "fleet-operators"], roles.AllowedRoles);
        }
    }

    [Fact]
    public void The_read_role_and_policy_names_are_the_cross_package_contract()
    {
        Assert.Equal("goldpath-ops-read", GoldpathPolicies.OpsRead);
        Assert.Equal("goldpath-ops-read", new GoldpathAuthOptions().OpsReadRole);
    }

    [Fact]
    public async Task A_viewer_reads_and_every_verb_answers_a_worded_403_envelope_naming_the_ops_role()
    {
        await using var app = await StartAsync();
        var viewer = As(app, "goldpath-ops-read");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/goldpath/admin/demo/things")).StatusCode);
        foreach (var verb in new[] { viewer.PostAsync("/goldpath/admin/demo/things/t1/pause", null), viewer.DeleteAsync("/goldpath/admin/demo/things/t1") })
        {
            using var refused = await verb;
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.False(envelope.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("this verb needs the goldpath-ops role; this identity can read this surface but not act on it",
                envelope.RootElement.GetProperty("message").GetString());
        }
    }

    [Theory]
    [InlineData("/goldpath/admin/demo/both")]
    [InlineData("/goldpath/admin/demo/any")]
    public async Task On_a_route_that_also_answers_a_verb_a_viewer_reads_and_every_verb_is_refused_in_words(string path)
    {
        // A route mapped with GET plus another method, or with no method at all, counts as a verb route: the floor is
        // attached, yet a GET on it still passes, and every other method meets the ops floor.
        await using var app = await StartAsync();
        var viewer = As(app, "goldpath-ops-read");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(path)).StatusCode);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var refused = await viewer.SendAsync(new HttpRequestMessage(method, path));
            if (path.EndsWith("/both", StringComparison.Ordinal) && method != HttpMethod.Post)
            {
                Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);   // the route does not answer it at all
                continue;
            }

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var envelope = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal("this verb needs the goldpath-ops role; this identity can read this surface but not act on it",
                envelope.RootElement.GetProperty("message").GetString());
        }

        var operatorClient = As(app, "goldpath-ops");
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.PostAsync(path, null)).StatusCode);
    }

    [Fact]
    public async Task An_operator_reads_and_acts_as_before()
    {
        await using var app = await StartAsync();
        var operatorClient = As(app, "goldpath-ops");

        Assert.Equal(HttpStatusCode.OK, (await operatorClient.GetAsync("/goldpath/admin/demo/things")).StatusCode);
        var paused = await operatorClient.PostAsync("/goldpath/admin/demo/things/t1/pause", null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        Assert.Equal("paused t1", (await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.OK, (await operatorClient.DeleteAsync("/goldpath/admin/demo/things/t1")).StatusCode);
    }

    [Fact]
    public async Task No_identity_is_challenged_and_an_identity_without_either_role_reads_nothing()
    {
        await using var app = await StartAsync();
        var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/goldpath/admin/demo/things")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/goldpath/admin/demo/things/t1/pause", null)).StatusCode);

        var stranger = As(app, "someone-else");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/goldpath/admin/demo/things")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync("/goldpath/admin/demo/things/t1/pause", null)).StatusCode);
    }

    [Fact]
    public async Task Renamed_roles_are_the_ones_demanded_and_the_ones_the_refusal_names()
    {
        await using var app = await StartAsync(o =>
        {
            o.OpsRole = "fleet-operators";
            o.OpsReadRole = "fleet-viewers";
        });

        var viewer = As(app, "fleet-viewers");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/goldpath/admin/demo/things")).StatusCode);
        var refused = await viewer.PostAsync("/goldpath/admin/demo/things/t1/pause", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.StartsWith("this verb needs the fleet-operators role;",
            (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await As(app, "fleet-operators").PostAsync("/goldpath/admin/demo/things/t1/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "goldpath-ops-read").GetAsync("/goldpath/admin/demo/things")).StatusCode);
    }

    [Fact]
    public async Task An_app_that_never_registers_the_read_policy_keeps_the_ops_floor_on_reads_and_is_told_why()
    {
        // No Goldpath.Auth: the app registers its own ops policy and nothing else, as one written before R4 does.
        var log = new CapturingLoggerProvider();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(log);
        builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o => o.Audience = TestIdp.Audience);
        TestIdp.WireValidation(builder.Services);
        builder.Services.AddAuthorization(o => o.AddPolicy(GoldpathPolicies.Ops, policy => policy.RequireRole("goldpath-ops")));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        MapSurface(app);
        await app.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await As(app, "goldpath-ops-read").GetAsync("/goldpath/admin/demo/things")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(app, "goldpath-ops").GetAsync("/goldpath/admin/demo/things")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(app, "goldpath-ops").PostAsync("/goldpath/admin/demo/things/t1/pause", null)).StatusCode);
        var line = Assert.Single(log.Lines, l => l.Category == "Goldpath.AdminSurface");
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("no 'goldpath-ops-read' policy is registered", line.Message, StringComparison.Ordinal);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Message)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<(string, LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
