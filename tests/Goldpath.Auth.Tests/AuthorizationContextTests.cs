using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Tests;

/// <summary>Direct coverage of the Mediant authorization context and the auth meters.</summary>
public sealed class AuthorizationContextTests
{
    private sealed class FixedAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static HttpContext ContextWith(params Claim[] claims)
        => new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")),
        };

    [Fact]
    public void UserId_walks_nameidentifier_then_sub_then_name_then_empty()
    {
        Assert.Equal("u-1", new HttpClaimsAuthorizationContext(new FixedAccessor(
            ContextWith(new Claim(ClaimTypes.NameIdentifier, "u-1"), new Claim("sub", "s-1")))).UserId);

        Assert.Equal("s-1", new HttpClaimsAuthorizationContext(new FixedAccessor(
            ContextWith(new Claim("sub", "s-1")))).UserId);

        Assert.Equal("", new HttpClaimsAuthorizationContext(new FixedAccessor(null)).UserId);
    }

    [Fact]
    public void Roles_union_mapped_and_raw_types_and_authentication_reflects_the_identity()
    {
        var authz = new HttpClaimsAuthorizationContext(new FixedAccessor(ContextWith(
            new Claim(ClaimTypes.Role, "mapped"),
            new Claim("role", "raw"),
            new Claim("roles", "plural"))));

        Assert.Equal(["mapped", "raw", "plural"], authz.Roles);
        Assert.True(authz.IsAuthenticated);
        Assert.False(new HttpClaimsAuthorizationContext(new FixedAccessor(null)).IsAuthenticated);
    }

    [Fact]
    public void HasClaim_matches_type_and_value_exactly()
    {
        var authz = new HttpClaimsAuthorizationContext(new FixedAccessor(ContextWith(
            new Claim("department", "risk"))));

        Assert.True(authz.HasClaim("department", "risk"));
        Assert.False(authz.HasClaim("department", "sales"));
        Assert.False(authz.HasClaim("team", "risk"));
        Assert.False(new HttpClaimsAuthorizationContext(new FixedAccessor(null)).HasClaim("department", "risk"));
    }

    // The "Goldpath.Auth" meter is process-wide, so a MeterListener also hears every other
    // test class that xUnit runs in parallel (AuthContractTests refuses bindings too: #310).
    // A measurement callback runs synchronously on the recording call's own flow, so this
    // marker, set around THIS test's call, tells its measurements apart from a neighbour's.
    private static readonly AsyncLocal<bool> s_ownFlow = new();

    [Fact]
    public async Task Binding_rejects_and_api_key_failures_hit_their_meters()
    {
        var bindingRejects = 0L;
        var failures = new List<string?>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Goldpath.Auth")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "goldpath_auth_tenant_binding_rejects_total" && s_ownFlow.Value)
            {
                Interlocked.Add(ref bindingRejects, value);
            }
            else if (instrument.Name == "goldpath_auth_failures_total")
            {
                lock (failures)
                {
                    failures.Add(tags.ToArray().FirstOrDefault(t => t.Key == "reason").Value as string);
                }
            }
        });
        listener.Start();

        // Binding reject: acme token on a globex-resolved request.
        var options = new GoldpathAuthOptions();
        var middleware = new GoldpathTenantBindingMiddleware(_ => Task.CompletedTask, options);
        var context = ContextWith(new Claim(options.TenantClaim, "acme"));
        context.Response.Body = new MemoryStream();
        using (GoldpathTenant.Use("globex"))
        {
            s_ownFlow.Value = true;
            try
            {
                await middleware.InvokeAsync(context);
            }
            finally
            {
                s_ownFlow.Value = false;
            }
        }

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(1, Interlocked.Read(ref bindingRejects));

        // ApiKey failure reason lands with its tag (via the real handler pipeline).
        using var app = await AuthTestHost.StartApiKeyAsync();
        var wrong = app.GetTestClient();
        wrong.DefaultRequestHeaders.Add(GoldpathHeaders.ApiKey, "wrong-key!");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/secure")).StatusCode);
        lock (failures)
        {
            Assert.Contains("invalid-api-key", failures);
        }
    }
}

internal static class AuthTestHost
{
    public static async Task<IHost> StartApiKeyAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.AddGoldpathAuth(o =>
        {
            o.Strategy = GoldpathAuthStrategy.ApiKey;
            o.ApiKeys["job"] = "right-key";
        });
        var app = builder.Build();
        app.UseGoldpathAuth();
        app.MapGet("/secure", () => "in");
        await app.StartAsync();
        return app;
    }
}
