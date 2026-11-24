using Microsoft.Extensions.DependencyInjection;

namespace Goldpath;

/// <summary>
/// The batch's tenant as the ambient tenant of a background flow (#539): the execute run and a replay are not HTTP
/// requests, so nothing else sets it. Only a multi-tenant app (one that composes <see cref="GoldpathMultiTenancyMarker"/>,
/// the test <c>AdminTenantScope</c> uses) has its ambient tenant pinned; a single-tenant app's batch label, whatever it
/// says, leaves the ambient tenant as it was, exactly as before #539. The previous value is restored when the scope ends.
/// </summary>
internal static class BatchTenant
{
    /// <summary>
    /// What a batch's stored tenant means for <paramref name="services"/>: nothing to pin (single-tenant, or no tenant), a
    /// tenant to pin, or — on a multi-tenant app — a stored value that is not a tenant id, which every row fails with.
    /// </summary>
    public static Pin Resolve(IServiceProvider services, string? tenant)
    {
        if (tenant is null || services.GetService<GoldpathMultiTenancyMarker>() is null)
        {
            return Pin.None;
        }

        return TenantId.TryCreate(tenant, out var id)
            ? new Pin(id, null)
            : new Pin(null, $"the batch's stored tenant '{Clip(tenant)}' is not a tenant id (1-64 characters from [a-z0-9-]); no row of it can run as a tenant");
    }

    /// <summary>Pins <paramref name="tenant"/> as the ambient tenant until disposed; nothing is pinned when it is null.</summary>
    public static IDisposable Use(TenantId? tenant) => tenant is { } id ? new Scope(id) : NoScope.Instance;

    private static string Clip(string value) => value.Length <= 80 ? value : value[..80] + "…";

    /// <summary>A resolved batch tenant: the id to pin, or why no row can run.</summary>
    /// <param name="Tenant">The tenant to pin; null when there is nothing to pin.</param>
    /// <param name="Refusal">Why the batch's rows cannot run as its tenant; null when they can.</param>
    public sealed record Pin(TenantId? Tenant, string? Refusal)
    {
        /// <summary>Nothing to pin, nothing refused.</summary>
        public static Pin None { get; } = new(null, null);
    }

    // The same shape as GoldpathTenant.UseScope, kept here on purpose: Goldpath.Bulk does not reference
    // Goldpath.MultiTenancy, where that scope lives, and must not for one IDisposable.
    private sealed class Scope : IDisposable
    {
        private readonly TenantId? _previous;

        public Scope(TenantId tenant)
        {
            _previous = GoldpathAmbientTenant.Current;
            GoldpathAmbientTenant.Current = tenant;
        }

        public void Dispose() => GoldpathAmbientTenant.Current = _previous;
    }

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }
}
