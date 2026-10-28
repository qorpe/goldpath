using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Goldpath;

/// <summary>
/// The H2 auth floor, in ONE place: every admin mapper compiles THIS file (Goldpath.Sdk), so the fail-closed default cannot drift per package (review-agent finding on
/// PR #14 — accepted). Apply the ops policy by default; the opt-out is a visible,
/// warning-logged decision.
/// </summary>
public static class AdminSurfaceGuard
{
    /// <summary>
    /// Applies the ops floor to <paramref name="group"/> unless <paramref name="exposeUnsecured"/> — a visible,
    /// warning-logged opt-out. The HTTP method decides (admin-contract revision R4.1): a <c>GET</c> or <c>HEAD</c>
    /// demands <see cref="GoldpathPolicies.OpsRead"/>, and every other method also demands
    /// <see cref="GoldpathPolicies.Ops"/>, refused with a <c>403</c> envelope that says which role is missing. An
    /// app that never registered the read policy keeps the floor it had: <see cref="GoldpathPolicies.Ops"/> on
    /// every route, and one information line naming the policy to register.
    /// </summary>
    public static void Apply(IEndpointRouteBuilder endpoints, RouteGroupBuilder group, string prefix, bool exposeUnsecured)
    {
        var logger = endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Goldpath.AdminSurface");
        if (exposeUnsecured)
        {
            logger.LogWarning("{Prefix} is mapped WITHOUT the ops policy (exposeUnsecured: true) — acceptable only behind an authenticating boundary.", prefix);
            return;
        }

        var policies = endpoints.ServiceProvider.GetService<IOptions<AuthorizationOptions>>()?.Value;
        if (policies?.GetPolicy(GoldpathPolicies.OpsRead) is null)
        {
            // An unregistered policy would answer 500 at request time; the floor stays whole instead.
            logger.LogInformation(
                "{Prefix} demands {Ops} for reads too: no '{OpsRead}' policy is registered. Register it (Goldpath.Auth does) to let a viewer read without acting.",
                prefix, GoldpathPolicies.Ops, GoldpathPolicies.OpsRead);
            group.RequireAuthorization(GoldpathPolicies.Ops);
            return;
        }

        group.RequireAuthorization(GoldpathPolicies.OpsRead);
        var refusal = RefusalFor(policies.GetPolicy(GoldpathPolicies.Ops));
        ((IEndpointConventionBuilder)group).Add(endpoint =>
        {
            if (IsRead(endpoint.Metadata.OfType<IHttpMethodMetadata>().LastOrDefault()))
            {
                return;
            }

            endpoint.Metadata.Add(AdminVerbFloor.Instance);
            endpoint.FilterFactories.Add((_, next) => async invocation =>
            {
                var http = invocation.HttpContext;
                if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
                {
                    return await next(invocation);
                }

                var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
                return (await authorization.AuthorizeAsync(http.User, GoldpathPolicies.Ops)).Succeeded
                    ? await next(invocation)
                    : TypedResults.Json(new AdminRefusal(false, refusal), statusCode: StatusCodes.Status403Forbidden);
            });
        });
    }

    // A route that answers GET or HEAD only is a read; one that names no method, or any other, is a verb.
    private static bool IsRead(IHttpMethodMetadata? methods)
        => methods is { HttpMethods.Count: > 0 }
            && methods.HttpMethods.All(m => HttpMethods.IsGet(m) || HttpMethods.IsHead(m));

    // The sentence names the role the ops policy actually demands, so a renamed role is named as configured.
    private static string RefusalFor(AuthorizationPolicy? ops)
    {
        var roles = ops?.Requirements.OfType<RolesAuthorizationRequirement>().SelectMany(r => r.AllowedRoles).ToList() ?? [];
        var role = roles.Count == 0 ? GoldpathPolicies.Ops : string.Join(" or ", roles);
        return $"this verb needs the {role} role; this identity can read this surface but not act on it";
    }

    /// <summary>The admin envelope a refused verb answers with: <c>{ "ok": false, "message": "…" }</c>.</summary>
    private sealed record AdminRefusal(bool Ok, string Message);
}

/// <summary>
/// Marks an admin route that only an operator may act on (admin-contract revision R4.1): the guard's verb filter is
/// on it. The route-table test reads this marker, so a route mapped outside the guard cannot pass as guarded.
/// </summary>
internal sealed class AdminVerbFloor
{
    public static readonly AdminVerbFloor Instance = new();

    private AdminVerbFloor()
    {
    }
}
