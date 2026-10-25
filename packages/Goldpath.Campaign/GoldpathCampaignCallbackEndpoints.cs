using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Goldpath;

/// <summary>What a target system reports back for an accepted item (campaign RFC R2.1).</summary>
public sealed record GoldpathCampaignCallbackRequest(
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null,
    bool Retryable = true);

/// <summary>
/// The callback signature (campaign RFC R2.8): HMAC-SHA256, keyed by the campaign type's
/// secret, over <c>{timestamp}\n{METHOD}\n{path}\n</c> followed by the raw body bytes.
/// <c>path</c> is the request path exactly as sent (no query string); <c>timestamp</c> is unix
/// seconds and travels in <see cref="TimestampHeader"/>; the signature travels in
/// <see cref="SignatureHeader"/> as <c>v1=&lt;lowercase hex&gt;</c>. A .NET target system (or a
/// relay in front of one) signs with <see cref="Sign"/>; any other stack reproduces the same
/// three lines.
/// </summary>
public static class GoldpathCampaignCallbackSignature
{
    /// <summary>The header carrying the unix-seconds timestamp the signature covers.</summary>
    public const string TimestampHeader = "X-Goldpath-Timestamp";

    /// <summary>The header carrying <c>v1=&lt;hex&gt;</c>.</summary>
    public const string SignatureHeader = "X-Goldpath-Signature";

    /// <summary>The shortest secret a type may sign with — an HMAC-SHA256 key's own width.</summary>
    public const int MinimumSecretBytes = 32;

    private const string Version = "v1=";

    /// <summary>Computes the <see cref="SignatureHeader"/> value for one callback request.</summary>
    public static string Sign(string secret, string method, string path, long timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return Version + Convert.ToHexString(Compute(Encoding.UTF8.GetBytes(secret), method, path, timestamp, body)).ToLowerInvariant();
    }

    /// <summary>
    /// True when <paramref name="header"/> is the signature of the request under ANY of the
    /// active <paramref name="secrets"/> (two during a rotation). Constant-time per secret.
    /// </summary>
    internal static bool Verify(IReadOnlyList<byte[]> secrets, string method, string path, long timestamp, ReadOnlySpan<byte> body, string? header)
    {
        if (header is null || !header.StartsWith(Version, StringComparison.Ordinal) || header.Length != Version.Length + 64)
        {
            return false;
        }

        byte[] presented;
        try
        {
            presented = Convert.FromHexString(header.AsSpan(Version.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var match = false;
        foreach (var secret in secrets)
        {
            // Every secret is tried — no early exit, so the timing says nothing about which one matched.
            match |= CryptographicOperations.FixedTimeEquals(Compute(secret, method, path, timestamp, body), presented);
        }

        return match;
    }

    private static byte[] Compute(byte[] key, string method, string path, long timestamp, ReadOnlySpan<byte> body)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{timestamp}\n{method.ToUpperInvariant()}\n{path}\n")));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }
}

/// <summary>
/// The callback surface for ASYNCHRONOUS target systems (campaign RFC R2.1, R2.8): an item
/// whose handler answered <see cref="GoldpathCampaignActionResult.Accepted"/> waits here for
/// the provider's own word. The caller is a machine, not an operator, so it carries no ops
/// credential: every request is authenticated by its HMAC signature with the campaign type's
/// secret (<see cref="GoldpathCampaignTypeBuilder{TTarget}.SignedCallbacks"/>), inside a
/// timestamp window. An unsigned callback is not possible — a type without a secret has no
/// surface, and mapping the surface with no signing type refuses to start (GP1704 says it
/// at build time). Idempotent by construction: a repeat for an item that already settled
/// answers 200 and publishes nothing.
/// </summary>
public static class GoldpathCampaignCallbackEndpoints
{
    /// <summary>The largest callback body read; a target system's verdict is a few hundred bytes.</summary>
    private const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// Maps <c>POST {prefix}/{type}/{campaignId}/{correlationId}</c>. The route names the
    /// campaign, so a correlation id is looked up inside ONE campaign. Hand the target system
    /// that path (the handler knows <see cref="GoldpathCampaignItemContext.Type"/> and
    /// <see cref="GoldpathCampaignItemContext.CampaignId"/>). <paramref name="policy"/> is an
    /// OPTIONAL authorization policy enforced on top of the signature — for an adopter whose
    /// boundary also authenticates the caller; by default the signature alone authenticates.
    /// </summary>
    public static IEndpointRouteBuilder MapGoldpathCampaignCallbacks<TContext>(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/goldpath/campaign/callbacks",
        string? policy = null)
        where TContext : DbContext
    {
        var options = endpoints.ServiceProvider.GetRequiredService<GoldpathCampaignOptions>();
        if (!options.Types.Any(t => t.SignsCallbacks))
        {
            throw new InvalidOperationException(
                "goldpath: MapGoldpathCampaignCallbacks is mapped but no campaign type declares SignedCallbacks(secret) — "
                + "callbacks are authenticated by their HMAC signature and an unsigned one is not possible (campaign RFC R2.8, GP1704). "
                + "Add .SignedCallbacks(builder.Configuration[\"…\"]) to the type whose handler answers Accepted, or remove the mapping.");
        }

        var group = endpoints.MapGroup(prefix);
        if (policy is null)
        {
            group.AllowAnonymous();   // the signature IS the authentication; the app's fallback policy must not demand a user
        }
        else
        {
            group.RequireAuthorization(policy);
        }

        group.MapPost("/{type}/{campaignId:guid}/{correlationId}", async (
            string type, Guid campaignId, string correlationId, HttpContext http,
            [FromServices] GoldpathCampaignEngine<TContext> engine, CancellationToken ct) =>
        {
            if (!options.TypeMap.TryGetValue(type, out var campaignType) || !campaignType.SignsCallbacks)
            {
                return Results.NotFound(new GoldpathAdminResult(false, $"No signed callback surface for campaign type '{type}'."));
            }

            var body = await ReadBodyAsync(http.Request, ct);
            if (body is null)
            {
                return Results.Json(new GoldpathAdminResult(false, $"A callback body is at most {MaxBodyBytes} bytes."),
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            if (!Authentic(http, campaignType, options, body))
            {
                GoldpathCampaignMetrics.CallbacksRejected(type);
                return Results.Json(new GoldpathAdminResult(false, "The callback's signature is missing, stale or wrong."),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            GoldpathCampaignCallbackRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<GoldpathCampaignCallbackRequest>(body, JsonOptions(http));
            }
            catch (JsonException)
            {
                request = null;
            }

            if (request is null)
            {
                return Results.BadRequest(new GoldpathAdminResult(false, "The callback body is not a campaign callback ({\"succeeded\": true|false, …})."));
            }

            // The DbContext and the publisher come off the request scope: a generic [FromServices] TContext
            // parameter trips the route-handler analyzer (NRE) on net10, and the scope is the same either way.
            var db = http.RequestServices.GetRequiredService<TContext>();
            var resolution = await engine.ResolveCallbackAsync(db, type, campaignId, correlationId, request, ct);
            switch (resolution.Status)
            {
                case GoldpathCampaignCallbackStatus.Unknown:
                    return Results.NotFound(new GoldpathAdminResult(false,
                        $"No item of campaign {campaignId:N} ('{type}') carries correlation id '{correlationId}', and none is in flight to carry it."));
                case GoldpathCampaignCallbackStatus.AlreadySettled:
                    return Results.Ok(new GoldpathAdminResult(true,
                        $"Item for '{correlationId}' already settled — the callback is idempotent, nothing published."));
                case GoldpathCampaignCallbackStatus.Parked:
                    return Results.Json(new GoldpathAdminResult(true,
                        $"'{correlationId}' answered before its acceptance landed — parked; it settles the moment the acceptance does."),
                        statusCode: StatusCodes.Status202Accepted);
                default:
                    await http.RequestServices.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(resolution.Outcome!, ct);
                    return Results.Json(new GoldpathAdminResult(true,
                        $"Outcome for '{correlationId}' published ({(request.Succeeded ? "succeeded" : "failed")})."), statusCode: StatusCodes.Status202Accepted);
            }
        });

        return endpoints;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool Authentic(HttpContext http, GoldpathCampaignType type, GoldpathCampaignOptions options, byte[] body)
    {
        if (!long.TryParse(http.Request.Headers[GoldpathCampaignCallbackSignature.TimestampHeader], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
        {
            return false;
        }

        var now = (http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp) > options.CallbackTimestampTolerance.TotalSeconds)
        {
            return false;   // outside the window: a replay of a captured callback, or a clock that needs fixing
        }

        return GoldpathCampaignCallbackSignature.Verify(type.CallbackSecrets, http.Request.Method, SignedPath(http), timestamp, body,
            http.Request.Headers[GoldpathCampaignCallbackSignature.SignatureHeader].ToString());
    }

    /// <summary>The path exactly as the target system sent it (the raw request target, query stripped).</summary>
    private static string SignedPath(HttpContext http)
    {
        var raw = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (!string.IsNullOrEmpty(raw) && raw.StartsWith('/'))
        {
            var query = raw.IndexOf('?', StringComparison.Ordinal);
            return query < 0 ? raw : raw[..query];
        }

        return (http.Request.PathBase + http.Request.Path).ToUriComponent();
    }

    private static JsonSerializerOptions JsonOptions(HttpContext http)
        => http.RequestServices.GetService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()?.Value.SerializerOptions
            ?? WebJson;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}

/// <summary>Where a callback landed (campaign RFC R2.1).</summary>
public enum GoldpathCampaignCallbackStatus
{
    /// <summary>No item of the campaign carries the id, and no claim is in flight that could still accept it.</summary>
    Unknown = 0,

    /// <summary>The item carrying the id already settled (or moved on): an idempotent repeat, nothing to publish.</summary>
    AlreadySettled = 1,

    /// <summary>The item awaits this answer: <see cref="GoldpathCampaignCallbackResolution.Outcome"/> is to be published.</summary>
    Pending = 2,

    /// <summary>The answer outran its acceptance: parked, and settled by the sink when the acceptance lands.</summary>
    Parked = 3,
}

/// <summary>How a callback resolved; <see cref="Outcome"/> is set only when <see cref="Status"/> is Pending.</summary>
public sealed record GoldpathCampaignCallbackResolution(GoldpathCampaignCallbackStatus Status, GoldpathCampaignOutcomeMessage? Outcome);
