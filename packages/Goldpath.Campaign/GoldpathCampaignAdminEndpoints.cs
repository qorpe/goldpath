using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>The create-verb request body.</summary>
public sealed record GoldpathCampaignCreateRequest(
    string Type,
    string Name,
    Dictionary<string, string>? Parameters,
    GoldpathCampaignThrottle? Policy,
    string? Tenant);

/// <summary>The abort-verb request body (the reason becomes evidence).</summary>
public sealed record GoldpathCampaignAbortRequest(string Reason);

/// <summary>
/// The campaign admin API (`/goldpath/admin/campaign`, campaign RFC §4). Mount on the
/// management head, behind the auth floor. Every mutating verb carries the actor and
/// lands in the audit table; item REPLAY stays with the jobs console (`replay-items`).
/// </summary>
public static class GoldpathCampaignAdminEndpoints
{
    /// <summary>Maps the campaign admin API under <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapGoldpathCampaignAdmin<TContext>(this IEndpointRouteBuilder endpoints, string prefix = "/goldpath/admin/campaign", bool exposeUnsecured = false)
        where TContext : DbContext
    {
        var group = endpoints.MapGroup(prefix);
        AdminSurfaceGuard.Apply(endpoints, group, prefix, exposeUnsecured);

        // R1: campaign rows carry no tenant column — on a multi-tenant app this surface is
        // inherently cross-tenant, so the WHOLE group demands the all-tenants privilege.
        group.AddEndpointFilter(async (context, next) =>
            await AdminTenantScope.RequireAllTenantsAsync(context.HttpContext) is { } refusal ? refusal : await next(context));
        // R3: ?state= repeats — several states are OR'd; absent means all (additive, R2-compatible).
        group.MapGet("/", ([FromQuery] string[]? state, int? take, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => admin.ListAsync(state, take ?? 50, ct));

        group.MapGet("/{id:guid}", async (Guid id, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => await admin.GetAsync(id, ct) is { } info ? Results.Ok(info) : Results.NotFound());

        // Contract freeze (H8 D1): execution failures answer to ONE noun across modules —
        // `failures` (bulk's `/errors` is the VALIDATION report, a different concept).
        group.MapGet("/{id:guid}/failures", (Guid id, int? take, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => admin.GetFailedItemsAsync(id, take ?? 100, ct));

        group.MapGet("/{id:guid}/audit", (Guid id, int? take, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => admin.GetAuditAsync(id, take ?? 100, ct));

        group.MapPost("/", async ([FromBody] GoldpathCampaignCreateRequest request, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.CreateAsync(request.Type, request.Name,
                request.Parameters ?? [], request.Policy, request.Tenant, Actor(http), ct)));

        group.MapPost("/{id:guid}/pause", async (Guid id, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.PauseAsync(id, Actor(http), ct)));

        group.MapPost("/{id:guid}/resume", async (Guid id, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.ResumeAsync(id, Actor(http), ct)));

        group.MapPost("/{id:guid}/abort", async (Guid id, [FromBody] GoldpathCampaignAbortRequest request, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.AbortAsync(id, request.Reason, Actor(http), ct)));

        group.MapPost("/{id:guid}/throttle", async (Guid id, [FromBody] GoldpathCampaignThrottle patch, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.ThrottleAsync(id, patch, Actor(http), ct)));

        // Admin-contract R4.5 (campaign R3.2): the live ceilings. The routes sit in this group, so the all-tenants filter
        // above and R4.1's split (a viewer reads, an operator changes) apply unchanged; `{id:guid}` never matches them.
        group.MapGet("/ceilings", ([FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => admin.GetCeilingsAsync(ct));

        group.MapPost("/ceilings/global/throttle", async ([FromBody] GoldpathCampaignCeilingPatch patch, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.ThrottleCeilingAsync(GoldpathCampaignCeilingKind.Global, GoldpathCampaignCeiling.GlobalName, patch, Actor(http), ct)));

        // An undeclared name answers 400 naming the declared ones, not 404: the verb's subject is a name, not an entity looked up.
        group.MapPost("/ceilings/target-systems/{name}/throttle", async (string name, [FromBody] GoldpathCampaignCeilingPatch patch, HttpContext http, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => AsHttp(await admin.ThrottleCeilingAsync(GoldpathCampaignCeilingKind.TargetSystem, name, patch, Actor(http), ct)));

        group.MapGet("/ceilings/audit", (int? take, [FromServices] GoldpathCampaignAdminService<TContext> admin, CancellationToken ct)
            => admin.GetCeilingAuditAsync(take ?? 100, ct));

        return endpoints;
    }

    private static string Actor(HttpContext http)
        => http.User.Identity?.Name ?? "anonymous";

    private static IResult AsHttp(GoldpathAdminResult result)
        => result.Ok ? Results.Ok(result) : Results.BadRequest(result);
}
