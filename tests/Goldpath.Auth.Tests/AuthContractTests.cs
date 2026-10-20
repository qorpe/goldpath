using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// The auth module's remaining promises, each asserted where an app meets it: the ops roles
/// behind the admin floor (H2, admin-contract R1), the configuration section, the startup
/// audience guard (audit A3), HTTPS-only metadata, the failure meter, the binding's 403
/// (RFC D3), and the pipeline order <c>UseGoldpathAuth</c> owns on its own.
/// </summary>
public sealed class AuthContractTests
{
    private static async Task<WebApplication> StartAsync(
        Action<GoldpathAuthOptions> configure, bool wireTestIdp = true, Action<WebApplicationBuilder>? before = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        before?.Invoke(builder);
        builder.AddGoldpathAuth(configure);
        if (wireTestIdp)
        {
            TestIdp.WireValidation(builder.Services);
        }

        var app = builder.Build();
        app.UseGoldpathAuth();
        app.MapGet("/secure", (HttpContext ctx) => ctx.User.Identity?.Name ?? "in");
        app.MapGet("/ops", () => "ops").RequireAuthorization(GoldpathPolicies.Ops);
        app.MapGet("/ops-all", () => "all").RequireAuthorization(GoldpathPolicies.OpsAllTenants);
        await app.StartAsync();
        return app;
    }

    private static HttpClient Bearer(WebApplication app, string token)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void OpenIdForTests(GoldpathAuthOptions o)
    {
        o.Audience = TestIdp.Audience;
        o.RequireHttpsMetadata = false;
    }

    /// <summary>
    /// H2 + admin-contract R1: out of the box the ops floor demands <c>goldpath-ops</c>, and the
    /// cross-tenant widening demands <c>goldpath-ops-all-tenants</c> — two roles, two policies.
    /// </summary>
    [Fact]
    public async Task The_ops_policies_demand_their_default_roles()
    {
        await using var app = await StartAsync(OpenIdForTests);

        Assert.Equal(HttpStatusCode.OK, (await Bearer(app, TestIdp.Token(role: "goldpath-ops")).GetAsync("/ops")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Bearer(app, TestIdp.Token(role: "clerk")).GetAsync("/ops")).StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await Bearer(app, TestIdp.Token(role: "goldpath-ops-all-tenants")).GetAsync("/ops-all")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Bearer(app, TestIdp.Token(role: "goldpath-ops")).GetAsync("/ops-all")).StatusCode);
    }

    /// <summary>
    /// Audit A4: strategy <c>none</c> still registers BOTH ops policies, so the cross-tenant
    /// surface refuses with an honest 401 instead of crashing on an unknown policy.
    /// </summary>
    [Fact]
    public async Task Strategy_none_refuses_the_cross_tenant_floor_cleanly()
    {
        await using var app = await StartAsync(o => o.Strategy = GoldpathAuthStrategy.None, wireTestIdp: false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetTestClient().GetAsync("/ops-all")).StatusCode);
    }

    /// <summary>RFC §3: the manifest surface arrives as the <c>Goldpath:Auth</c> configuration section.</summary>
    [Fact]
    public async Task The_configuration_section_selects_the_strategy_and_the_keys()
    {
        await using var app = await StartAsync(_ => { }, wireTestIdp: false, before: builder =>
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Goldpath:Auth:Strategy"] = "ApiKey",
                ["Goldpath:Auth:ApiKeys:nightly-batch"] = "configured-key",
            }));

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(GoldpathHeaders.ApiKey, "configured-key");
        var response = await client.GetAsync("/secure");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nightly-batch", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Audit A3, both halves: the guard fires only for a REAL authority without an audience.
    /// No authority (the first-click dev app) and an empty one still start with the 401 floor;
    /// a real authority with a real audience starts; an empty audience counts as none.
    /// </summary>
    [Fact]
    public async Task The_audience_guard_fires_only_for_a_real_authority_without_an_audience()
    {
        await using (var devApp = await StartAsync(o => o.Audience = null, wireTestIdp: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await devApp.GetTestClient().GetAsync("/secure")).StatusCode);
        }

        await using (await StartAsync(o => { o.Authority = ""; o.Audience = null; }, wireTestIdp: false))
        {
        }

        await using (await StartAsync(o => { o.Authority = "https://idp.example"; o.Audience = "goldpath-api"; }, wireTestIdp: false))
        {
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(o => { o.Authority = "https://idp.example"; o.Audience = ""; }, wireTestIdp: false));
        Assert.Contains("AllowAnyAudience", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Options doc: metadata is HTTPS-only unless EXPLICITLY disabled — a plain-http authority
    /// is refused by the JwtBearer handler instead of fetching keys over cleartext.
    /// </summary>
    [Fact]
    public async Task A_plain_http_authority_is_refused_by_default()
    {
        await using var app = await StartAsync(
            o => { o.Authority = "http://idp.example"; o.Audience = "goldpath-api"; }, wireTestIdp: false);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.GetTestClient().GetAsync("/secure"));
        Assert.Contains("HTTPS", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>RFC §6: a rejected JWT lands on <c>goldpath_auth_failures_total</c> tagged with WHY.</summary>
    [Fact]
    public async Task A_rejected_token_hits_the_failure_meter_with_its_reason()
    {
        var reasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Goldpath.Auth" && instrument.Name == "goldpath_auth_failures_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "reason" && tag.Value is string reason && reason.EndsWith("Exception", StringComparison.Ordinal))
                {
                    lock (reasons)
                    {
                        reasons.Add(reason);
                    }
                }
            }
        });
        listener.Start();

        await using var app = await StartAsync(OpenIdForTests);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Bearer(app, TestIdp.Token(wrongKey: true)).GetAsync("/secure")).StatusCode);

        lock (reasons)
        {
            Assert.Contains(reasons, r => r.StartsWith("SecurityToken", StringComparison.Ordinal));
        }
    }

    /// <summary>RFC D4: a wrong key is a FAILED authentication with a teaching message, not a silent no-result.</summary>
    [Fact]
    public async Task A_wrong_api_key_fails_authentication_with_a_message()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddGoldpathAuth(o =>
        {
            o.Strategy = GoldpathAuthStrategy.ApiKey;
            o.ApiKeys["job"] = "right-key";
        });
        await using var app = builder.Build();
        app.UseGoldpathAuth();
        app.MapGet("/probe", async (HttpContext ctx) =>
            (await ctx.AuthenticateAsync(GoldpathApiKeyAuthenticationHandler.SchemeName)).Failure?.Message ?? "none")
            .AllowAnonymous();
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(GoldpathHeaders.ApiKey, "wrong-key");
        Assert.Equal("Invalid API key.", await client.GetStringAsync("/probe"));
    }

    /// <summary>
    /// RFC D3, the refusal itself: a contradicting token gets a 403 problem body typed with
    /// RFC 9110 §15.5.4 — and the request goes NO further down the pipeline.
    /// </summary>
    [Fact]
    public async Task A_binding_refusal_stops_the_pipeline_with_a_typed_problem()
    {
        var reachedNext = false;
        var middleware = new GoldpathTenantBindingMiddleware(_ => { reachedNext = true; return Task.CompletedTask; }, new GoldpathAuthOptions());
        var context = Authenticated(new Claim("goldpath_tenant", "acme"));
        using (GoldpathTenant.Use("globex"))
        {
            await middleware.InvokeAsync(context);
        }

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(reachedNext, "a refused request must not reach the endpoint");
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4", body, StringComparison.Ordinal);
    }

    /// <summary>RFC D3's opt-out: with <c>BindTenant</c> off the middleware never refuses, even on a contradiction.</summary>
    [Fact]
    public async Task With_binding_off_a_contradicting_token_passes_through()
    {
        var reachedNext = false;
        var middleware = new GoldpathTenantBindingMiddleware(
            _ => { reachedNext = true; return Task.CompletedTask; }, new GoldpathAuthOptions { BindTenant = false });
        var context = Authenticated(new Claim("goldpath_tenant", "acme"));
        using (GoldpathTenant.Use("globex"))
        {
            await middleware.InvokeAsync(context);
        }

        Assert.True(reachedNext);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// <c>UseGoldpathAuth</c> wires authentication ITSELF (its summary: authentication → binding →
    /// authorization) — a host without WebApplication's implicit <c>UseAuthentication</c>
    /// (a bare <see cref="IApplicationBuilder"/> pipeline) still authenticates.
    /// </summary>
    [Fact]
    public async Task UseGoldpathAuth_authenticates_without_web_applications_implicit_middleware()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddRouting();   // what any ASP.NET host registers; WebApplication is not involved
        builder.AddGoldpathAuth(o =>
        {
            o.Strategy = GoldpathAuthStrategy.ApiKey;
            o.ApiKeys["job"] = "right-key";
        });
        using var host = builder.Build();

        var app = new ApplicationBuilder(host.Services);
        app.UseGoldpathAuth();
        app.Run(ctx => ctx.Response.WriteAsync(ctx.User.Identity?.Name ?? "anonymous"));
        var pipeline = app.Build();

        using var scope = host.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers[GoldpathHeaders.ApiKey] = "right-key";
        context.Response.Body = new MemoryStream();
        await pipeline(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("job", context.User.Identity?.Name);
    }

    /// <summary>
    /// RFC §4: the OpenAPI document names the bearer FORMAT, and Goldpath's scheme joins the
    /// document without clobbering a scheme the app declared itself.
    /// </summary>
    [Fact]
    public async Task The_openapi_scheme_names_jwt_and_leaves_the_apps_own_schemes_alone()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddOpenApi(openApi => openApi.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["partner-basic"] = new Microsoft.OpenApi.OpenApiSecurityScheme
            {
                Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                Scheme = "basic",
            };
            return Task.CompletedTask;
        }));
        builder.AddGoldpathAuth(OpenIdForTests);
        TestIdp.WireValidation(builder.Services);
        await using var app = builder.Build();
        app.UseGoldpathAuth();
        app.MapOpenApi().AllowAnonymous();
        await app.StartAsync();

        var doc = await app.GetTestClient().GetStringAsync("/openapi/v1.json");
        Assert.Contains("\"bearerFormat\": \"JWT\"", doc, StringComparison.Ordinal);
        Assert.Contains("\"goldpath\"", doc, StringComparison.Ordinal);
        Assert.Contains("\"partner-basic\"", doc, StringComparison.Ordinal);
    }

    /// <summary>RFC D5: Mediant's <c>UserId</c> falls back to the identity NAME, and roles are never null.</summary>
    [Fact]
    public void The_authorization_context_falls_back_to_the_name_and_never_answers_null_roles()
    {
        var named = new HttpClaimsAuthorizationContext(
            new FixedAccessor(Authenticated(new Claim(ClaimTypes.Name, "batch-runner"))));
        Assert.Equal("batch-runner", named.UserId);

        var absent = new HttpClaimsAuthorizationContext(new FixedAccessor(null));
        Assert.NotNull(absent.Roles);
        Assert.Empty(absent.Roles);
    }

    /// <summary>An accessor that answers exactly one context (HttpContextAccessor's is ambient).</summary>
    private sealed class FixedAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static DefaultHttpContext Authenticated(params Claim[] claims)
        => new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
            Response = { Body = new MemoryStream() },
        };
}
