using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>
/// The file-exchange admin API (§7.1: the API is the contract; console federation rides
/// it). READ-ONLY on purpose: re-delivering the file through its transport IS the
/// reprocess (the engine dedups by construction), so this surface carries no verbs — an
/// "operator reprocess" without the file's bytes would be a lie. Mount on the management
/// head, behind the auth floor, or with the VISIBLE opt-out.
/// </summary>
public static class GoldpathFileExchangeAdminEndpoints
{
    /// <summary>Maps the file-exchange admin API under <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapGoldpathFileExchangeAdmin(this IEndpointRouteBuilder endpoints, string prefix = "/goldpath/admin/fileexchange", bool exposeUnsecured = false)
    {
        // Fail at STARTUP, in words, when the composed ledger cannot answer the reads —
        // a custom IGoldpathFileLedger owes the engine six calls, not the admin views.
        if (endpoints.ServiceProvider.GetRequiredService<IGoldpathFileLedger>() is not IGoldpathFileLedgerQueries)
        {
            throw new InvalidOperationException(
                "MapGoldpathFileExchangeAdmin needs a ledger that also implements IGoldpathFileLedgerQueries — both shipped ledgers do; a custom ledger must add the two read calls (or not mount the admin surface).");
        }

        var group = endpoints.MapGroup(prefix);
        AdminSurfaceGuard.Apply(endpoints, group, prefix, exposeUnsecured);

        // R1 rule 5: the ledger carries no tenant column, so on a multi-tenant app this
        // surface is cross-tenant by nature — every read demands the all-tenants privilege,
        // as campaign's does. Without it a tenant-A operator would read every rail's files
        // and every quarantined row's reason. Single-tenant apps are unchanged.
        group.AddEndpointFilter(async (context, next) =>
            await AdminTenantScope.RequireAllTenantsAsync(context.HttpContext) is { } refusal ? refusal : await next(context));

        // The probe root: every declared rail with its counts (an app with rails and no files still answers 200 []).
        group.MapGet("/rails", ([FromServices] GoldpathFileExchangeAdminService admin, CancellationToken ct)
            => admin.GetRailsAsync(ct));

        // R3: ?rail= repeats — values OR within the filter.
        group.MapGet("/files", async ([FromQuery] string[]? rail, int? take, [FromServices] GoldpathFileExchangeAdminService admin, CancellationToken ct)
            => Results.Ok(await admin.GetFilesAsync(rail, take ?? 50, ct)));

        // R3: ?rail= and ?file= repeat — values OR within a filter, filters AND together.
        // File names carry dots and slashes, so they ride the QUERY, never the path.
        group.MapGet("/quarantine", async ([FromQuery] string[]? rail, [FromQuery] string[]? file, int? take, [FromServices] GoldpathFileExchangeAdminService admin, CancellationToken ct)
            => Results.Ok(await admin.GetQuarantineAsync(rail, file, take ?? 200, ct)));

        return endpoints;
    }
}
