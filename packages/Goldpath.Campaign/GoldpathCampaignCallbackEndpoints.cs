using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Goldpath;

/// <summary>What a target system reports back for an accepted item (campaign RFC R2.1).</summary>
public sealed record GoldpathCampaignCallbackRequest(
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null,
    bool Retryable = true);

/// <summary>
/// The callback surface for ASYNCHRONOUS target systems (campaign RFC R2.1): an item whose
/// handler answered <see cref="GoldpathCampaignActionResult.Accepted"/> waits here for the
/// provider's own word. This is NOT an admin surface — the caller is a machine, not an
/// operator — so it carries its own policy parameter rather than the ops floor; mount it
/// behind mTLS or a signed-request policy of your own. Idempotent by construction: a second
/// callback for an item that already settled answers 200 and publishes nothing.
/// </summary>
public static class GoldpathCampaignCallbackEndpoints
{
    /// <summary>Maps <c>POST {prefix}/{type}/{correlationId}</c>.</summary>
    public static IEndpointRouteBuilder MapGoldpathCampaignCallbacks<TContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/goldpath/campaign/callbacks",
        string policy = GoldpathPolicies.Ops,
        bool exposeUnsecured = false)
        where TContext : DbContext
    {
        var group = endpoints.MapGroup(prefix);
        if (exposeUnsecured)
        {
            endpoints.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Goldpath.CampaignCallbacks")
                .LogWarning("{Prefix} is mapped WITHOUT an authorization policy (exposeUnsecured: true) — acceptable only behind an authenticating boundary such as mTLS.", prefix);
        }
        else
        {
            group.RequireAuthorization(policy);
        }

        group.MapPost("/{type}/{correlationId}", async (
            string type, string correlationId, [FromBody] GoldpathCampaignCallbackRequest request,
            HttpContext http, [FromServices] GoldpathCampaignEngine<TContext> engine, CancellationToken ct) =>
        {
            // The DbContext and the publisher come off the request scope: a generic [FromServices] TContext
            // parameter trips the route-handler analyzer (NRE) on net10, and the scope is the same either way.
            var db = http.RequestServices.GetRequiredService<TContext>();
            var publisher = http.RequestServices.GetRequiredService<IPublishEndpoint>();
            var resolution = await engine.ResolveCallbackAsync(db, type, correlationId, request, ct);
            if (!resolution.Found)
            {
                return Results.NotFound(new GoldpathAdminResult(false,
                    $"No item of campaign type '{type}' carries correlation id '{correlationId}'."));
            }

            if (resolution.Outcome is null)
            {
                return Results.Ok(new GoldpathAdminResult(true,
                    $"Item for '{correlationId}' already settled — the callback is idempotent, nothing published."));
            }

            await publisher.Publish(resolution.Outcome, ct);
            return Results.Json(new GoldpathAdminResult(true,
                $"Outcome for '{correlationId}' published ({(request.Succeeded ? "succeeded" : "failed")})."), statusCode: StatusCodes.Status202Accepted);
        });

        return endpoints;
    }
}

/// <summary>How a callback resolved: unknown id, already settled (no outcome), or pending (outcome to publish).</summary>
public sealed record GoldpathCampaignCallbackResolution(bool Found, GoldpathCampaignOutcomeMessage? Outcome);
