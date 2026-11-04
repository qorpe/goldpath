using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>The gate payload: the decision's evidence (note mandatory for reject).</summary>
public sealed record GoldpathBulkDecisionRequest(string? Note);

/// <summary>
/// The bulk admin API (§7.1: the API is the contract; the console and the AI skills script
/// THIS). Mount on the management head, behind the auth floor — gate verbs stamp the
/// authenticated principal into the batch row. Run views live in the JOBS console (a batch
/// executes as a run); this surface owns the intake: upload, report, gate.
/// Uploads are raw octet-stream bodies ON PURPOSE: `curl --data-binary @payments.csv` is
/// the whole client story — no multipart ceremony, no antiforgery coupling.
/// </summary>
public static class GoldpathBulkAdminEndpoints
{
    /// <summary>Maps the bulk admin API under <paramref name="prefix"/>.</summary>
    public static IEndpointRouteBuilder MapGoldpathBulkAdmin<TContext>(this IEndpointRouteBuilder endpoints, string prefix = "/goldpath/admin/bulk", bool exposeUnsecured = false)
        where TContext : DbContext
    {
        var group = endpoints.MapGroup(prefix);
        AdminSurfaceGuard.Apply(endpoints, group, prefix, exposeUnsecured);
        group.MapGet("/definitions", ([FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct)
            => admin.GetDefinitionsAsync(ct));

        group.MapPost("/batches/{definition}", async (string definition, string? fileName, string? tenant, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, [FromServices] GoldpathBulkOptions options, CancellationToken ct) =>
        {
            var scope = await AdminTenantScope.ResolveAsync(http, tenant);
            if (scope.Refusal is not null)
            {
                return scope.Refusal;
            }

            // A name nobody registered is the caller's mistake, not the server's (#377): answer 404 with
            // the registered names, before the body is read, instead of letting Definition(name) throw a 500.
            if (options.Batches.All(b => b.Name != definition))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Unknown bulk definition",
                    detail: $"No bulk definition named '{definition}' — registered: {string.Join(", ", options.Batches.Select(b => b.Name))}.");
            }

            return Results.Ok(await admin.UploadAsync(definition, http.Request.Body, fileName ?? "upload.csv", scope.Tenant, Actor(http), ct));
        });

        // R3: ?state= and ?definition= repeat — values OR within a filter, filters AND together.
        // The tenant wrapper's ternary types the delegate as IResult, which hides the 200 from
        // the OpenAPI exporter (#98) — the list endpoints declare it.
        group.MapGet("/batches", async ([FromQuery] string[]? state, [FromQuery] string[]? definition, string? tenant, int? take, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct) =>
        {
            var scope = await AdminTenantScope.ResolveAsync(http, tenant);
            return scope.Refusal ?? Results.Ok(await admin.GetBatchesAsync(state, definition, scope.Tenant, take ?? 50, ct));
        }).Produces<IReadOnlyList<GoldpathBulkBatchInfo>>();

        group.MapGet("/batches/{batchId:guid}", async (Guid batchId, string? tenant, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct) =>
        {
            var scope = await AdminTenantScope.ResolveAsync(http, tenant);
            if (scope.Refusal is not null)
            {
                return scope.Refusal;
            }

            return await admin.GetBatchAsync(batchId, scope.Tenant, ct) is { } batch ? Results.Ok(batch) : Results.NotFound();
        });

        group.MapGet("/batches/{batchId:guid}/errors", async (Guid batchId, int? afterRow, int? take, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct)
            =>
        {
            var scope = await AdminTenantScope.ResolveAsync(http, null);
            return scope.Refusal ?? Results.Ok(await admin.GetErrorsAsync(batchId, scope.Tenant, afterRow ?? 0, take ?? 200, ct));
        }).Produces<IReadOnlyList<GoldpathBulkRowError>>();

        group.MapPost("/batches/{batchId:guid}/approve", async (Guid batchId, GoldpathBulkDecisionRequest? request, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct)
            => ToResult(await admin.ApproveAsync(batchId, Actor(http), request?.Note, ct)));

        group.MapPost("/batches/{batchId:guid}/reject", async (Guid batchId, GoldpathBulkDecisionRequest? request, HttpContext http, [FromServices] GoldpathBulkAdminService<TContext> admin, CancellationToken ct)
            => ToResult(await admin.RejectAsync(batchId, Actor(http), request?.Note, ct)));

        return endpoints;
    }

    private static string Actor(HttpContext http)
        => http.User.Identity?.Name ?? "anonymous";

    private static IResult ToResult(GoldpathAdminResult result)
        => result.Ok ? Results.Ok(result) : Results.BadRequest(result);
}
