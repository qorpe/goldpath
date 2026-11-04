using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Goldpath.Testing.AdminScope;

// Compile-linked into Goldpath.FileExchange.Tests: both tenant-less surfaces are proven
// against the same operator, so the two proofs cannot drift apart.

/// <summary>
/// The operator, as the ops floor sees one: a test authentication scheme that signs every
/// request in as <c>operator-a</c> with <c>goldpath-ops</c>, plus the cross-tenant role when
/// the request asks for it. The three policies are role checks, as Goldpath.Auth composes
/// them; a multi-tenant app is the marker <c>AddGoldpathMultiTenancy</c> registers, with
/// the ambient tenant fixed at <c>tenant-a</c>.
/// </summary>
internal static class OperatorScheme
{
    private const string SchemeName = "operator";
    private const string AllTenantsHeader = "X-Test-All-Tenants";

    public static void Compose(IServiceCollection services, bool multiTenant)
    {
        services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, Handler>(SchemeName, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(GoldpathPolicies.Ops, policy => policy.RequireRole(GoldpathPolicies.Ops))
            .AddPolicy(GoldpathPolicies.OpsRead, policy => policy.RequireRole(GoldpathPolicies.Ops, GoldpathPolicies.OpsRead))
            .AddPolicy(GoldpathPolicies.OpsAllTenants, policy => policy.RequireRole(GoldpathPolicies.OpsAllTenants));
        if (multiTenant)
        {
            services.AddSingleton<GoldpathMultiTenancyMarker>();
            services.AddSingleton<ITenantContext>(new FixedTenant(TenantId.Create("tenant-a")));
        }
    }

    public static HttpClient Client(WebApplication app, bool allTenants)
    {
        var client = app.GetTestClient();
        if (allTenants)
        {
            client.DefaultRequestHeaders.Add(AllTenantsHeader, "true");
        }

        return client;
    }

    private sealed class FixedTenant(TenantId tenant) : ITenantContext
    {
        public TenantId? Current => tenant;
    }

    private sealed class Handler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Name, "operator-a"), new(ClaimTypes.Role, GoldpathPolicies.Ops) };
            if (Request.Headers.ContainsKey(AllTenantsHeader))
            {
                claims.Add(new Claim(ClaimTypes.Role, GoldpathPolicies.OpsAllTenants));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
