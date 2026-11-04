using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Goldpath;

/// <summary>The decision body: the role the decider acts under, and the reason (mandatory on reject — the engine enforces it).</summary>
public sealed record GoldpathApprovalDecisionRequest(string Role, string? Reason);

/// <summary>
/// The approvals admin API (§7.1: the API is the contract; console federation rides it).
/// Reads come from the admin service; DECISIONS go through the engine unchanged — the
/// console is one more decider, under exactly the four-eyes, role, distinct-eyes and
/// mandatory-reason rules everyone else faces. The decider's identity is the caller's
/// principal, so the trail names a person, never "the console".
/// </summary>
public static class GoldpathApprovalsAdminEndpoints
{
    /// <summary>Maps the approvals admin API under <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapGoldpathApprovalsAdmin(this IEndpointRouteBuilder endpoints, string prefix = "/goldpath/admin/approvals", bool exposeUnsecured = false)
    {
        var group = endpoints.MapGroup(prefix);
        AdminSurfaceGuard.Apply(endpoints, group, prefix, exposeUnsecured);

        // R1 rule 5: approval requests carry no tenant column, so on a multi-tenant app this
        // surface is cross-tenant by nature — every read AND both decisions demand the
        // all-tenants privilege, as campaign's do. Without it a tenant-A operator would read
        // and DECIDE every tenant's requests. Single-tenant apps are unchanged.
        group.AddEndpointFilter(async (context, next) =>
            await AdminTenantScope.RequireAllTenantsAsync(context.HttpContext) is { } refusal ? refusal : await next(context));

        // R3: ?status= and ?ladder= repeat — values OR within a filter, filters AND together.
        group.MapGet("/requests", async ([FromQuery] string[]? status, [FromQuery] string[]? ladder, int? take,
            [FromServices] GoldpathApprovalsAdminService admin, CancellationToken ct)
            => Results.Ok(await admin.GetRequestsAsync(status, ladder, take ?? 50, ct)));

        group.MapGet("/requests/{id:guid}", async (Guid id, [FromServices] GoldpathApprovalsAdminService admin, CancellationToken ct)
            => await admin.GetRequestAsync(id, ct) is { } detail ? Results.Ok(detail) : Results.NotFound());

        group.MapPost("/requests/{id:guid}/approve", (Guid id, GoldpathApprovalDecisionRequest body, HttpContext http,
            [FromServices] GoldpathApprovalEngine engine, CancellationToken ct)
            => DecideAsync(engine, id, http, body, granted: true, ct));

        group.MapPost("/requests/{id:guid}/reject", (Guid id, GoldpathApprovalDecisionRequest body, HttpContext http,
            [FromServices] GoldpathApprovalEngine engine, CancellationToken ct)
            => DecideAsync(engine, id, http, body, granted: false, ct));

        return endpoints;
    }

    private static async Task<IResult> DecideAsync(GoldpathApprovalEngine engine, Guid id, HttpContext http,
        GoldpathApprovalDecisionRequest body, bool granted, CancellationToken ct)
    {
        // The principal IS the decider — four-eyes needs a real identity to compare with
        // the requester ("anonymous" on an unsecured surface is then one identity like
        // any other, and four-eyes still holds).
        var decidedBy = http.User.Identity?.Name ?? "anonymous";
        var outcome = await engine.DecideAsync(id, decidedBy, body.Role, granted, body.Reason ?? "", ct);
        // The frozen verb envelope: Ok carries the applied outcome, a refusal carries the
        // RULE's name as the message — failures speak, never a silent 200 (contract §7.1).
        var result = new GoldpathAdminResult(
            outcome == GoldpathApprovalDecisionOutcome.Applied,
            outcome.ToString());
        return result.Ok ? Results.Ok(result) : Results.BadRequest(result);
    }
}
