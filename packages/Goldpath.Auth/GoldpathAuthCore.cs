using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Mediant.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Goldpath;

/// <summary>Authentication strategy (RFC D1 — saml/ldap are schema-rejected strategic deferrals).</summary>
public enum GoldpathAuthStrategy
{
    /// <summary>OIDC/JWT bearer against the customer's IdP — the default.</summary>
    OpenId,

    /// <summary>Minimal API-key handler for internal/legacy callers (RFC D4).</summary>
    ApiKey,

    /// <summary>No auth wiring at all — internal services behind mTLS/gateway.</summary>
    None,
}

/// <summary>Tuning surface — bound from <c>Goldpath:Auth</c>.</summary>
public sealed class GoldpathAuthOptions
{
    /// <summary>The role the admin surfaces' ops policy requires (mapped to your IdP's group/role claim).</summary>
    public string OpsRole { get; set; } = "goldpath-ops";

    /// <summary>
    /// The role the admin surfaces' READ policy accepts (admin-contract revision R4.1): it reads every admin
    /// surface and acts on none. <see cref="OpsRole"/> satisfies the read policy too. Granting it grants the
    /// content of every archived document in the holder's tenant scope, since an archive entry's retrieval is a read.
    /// </summary>
    public string OpsReadRole { get; set; } = "goldpath-ops-read";

    /// <summary>
    /// The role that grants CROSS-TENANT admin access on a multi-tenant app (admin-contract
    /// revision R1) — required ON TOP of <see cref="OpsRole"/> to widen past the ambient tenant.
    /// </summary>
    public string OpsAllTenantsRole { get; set; } = "goldpath-ops-all-tenants";

    /// <summary>Active strategy.</summary>
    public GoldpathAuthStrategy Strategy { get; set; } = GoldpathAuthStrategy.OpenId;

    /// <summary>OIDC authority (the IdP's base URL); discovery does the rest.</summary>
    public string? Authority { get; set; }

    /// <summary>
    /// EXPLICIT opt-out of audience validation (audit A3): without an <c>Audience</c>,
    /// startup fails unless this is true — accepting any token the authority minted must
    /// be a visible decision, never a default.
    /// </summary>
    public bool AllowAnyAudience { get; set; }

    /// <summary>Expected audience; unset skips audience validation (single-audience IdPs).</summary>
    public string? Audience { get; set; }

    /// <summary>JWT claim carrying the token's tenant (RFC D3).</summary>
    public string TenantClaim { get; set; } = "goldpath_tenant";

    /// <summary>Reject a token whose tenant claim mismatches the resolved ambient tenant.</summary>
    public bool BindTenant { get; set; } = true;

    /// <summary>HTTPS-only metadata (never disable outside local development).</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Header carrying the API key.</summary>
    public string ApiKeyHeader { get; set; } = GoldpathHeaders.ApiKey;

    /// <summary>Client-name → key map; values belong in the secret store, not in files.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = [];
}

/// <summary>Module meters — flow into the Ring A OTel pipeline.</summary>
internal static class GoldpathAuthMetrics
{
    private static readonly Meter s_meter = new("Goldpath.Auth");

    public static readonly Counter<long> Failures =
        s_meter.CreateCounter<long>("goldpath_auth_failures_total");

    public static readonly Counter<long> BindingRejects =
        s_meter.CreateCounter<long>("goldpath_auth_tenant_binding_rejects_total");
}

/// <summary>
/// Rejects an authenticated request whose token-borne tenant contradicts the resolved
/// ambient tenant (RFC D3): a stolen acme token cannot ride a globex header. Claim absent
/// → binding not enforced (gateway-injected-header topologies stay valid).
/// </summary>
public sealed class GoldpathTenantBindingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GoldpathAuthOptions _options;

    /// <summary>Creates the middleware.</summary>
    public GoldpathTenantBindingMiddleware(RequestDelegate next, GoldpathAuthOptions options)
    {
        _next = next;
        _options = options;
    }

    /// <summary>Enforces the binding after authentication, before authorization.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.BindTenant
            && context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(_options.TenantClaim)?.Value is { Length: > 0 } tokenTenant
            && GoldpathAmbientTenant.Current is { } ambient
            && !string.Equals(tokenTenant, ambient.Value, StringComparison.Ordinal))
        {
            GoldpathAuthMetrics.BindingRejects.Add(1);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
                title = "Token is not valid for this tenant.",
                status = 403,
            });
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Minimal API-key authentication (RFC D4): key from the configured header, validated with
/// a constant-time comparison against the configured client map. The matching client name
/// becomes the principal — audit rows say WHO, not just "an api key".
/// </summary>
public sealed class GoldpathApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "GoldpathApiKey";

    private readonly GoldpathAuthOptions _authOptions;

    /// <summary>Creates the handler.</summary>
    public GoldpathApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GoldpathAuthOptions authOptions)
        : base(options, logger, encoder)
        => _authOptions = authOptions;

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers[_authOptions.ApiKeyHeader].FirstOrDefault() is not { Length: > 0 } presented)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        foreach (var (client, key) in _authOptions.ApiKeys)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            if (presentedBytes.Length == keyBytes.Length
                && CryptographicOperations.FixedTimeEquals(presentedBytes, keyBytes))
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, client), new Claim(ClaimTypes.Name, client)],
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        GoldpathAuthMetrics.Failures.Add(1, new KeyValuePair<string, object?>("reason", "invalid-api-key"));
        return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
    }
}

/// <summary>
/// Mediant's authorization context fed from HTTP claims — the composition point that makes
/// <c>[Authorize]</c> on commands see the same principal the endpoint saw (RFC D5).
/// </summary>
public sealed class HttpClaimsAuthorizationContext : IAuthorizationContext
{
    private readonly IHttpContextAccessor _accessor;

    /// <summary>Creates the context over the HTTP accessor.</summary>
    public HttpClaimsAuthorizationContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? User => _accessor.HttpContext?.User;

    // JWT handlers differ on inbound claim mapping (long URIs vs the raw "sub"/"role"
    // names) — accept both so the behavior never depends on a mapping flag.
    private static readonly string[] s_roleClaimTypes = [ClaimTypes.Role, "role", "roles"];

    /// <inheritdoc />
    public string UserId
        => User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User?.FindFirstValue("sub")
            ?? User?.Identity?.Name
            ?? string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles
        => User?.Claims
            .Where(c => s_roleClaimTypes.Contains(c.Type))
            .Select(c => c.Value)
            .ToArray() ?? [];

    /// <inheritdoc />
    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public bool HasClaim(string claimType, string claimValue)
        => User?.HasClaim(claimType, claimValue) == true;
}
