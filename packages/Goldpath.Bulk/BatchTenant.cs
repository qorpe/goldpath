namespace Goldpath;

/// <summary>
/// The batch's tenant as the ambient tenant of a background flow (#539): the execute run and a replay are not HTTP
/// requests, so nothing else sets it. A batch without a tenant (a single-tenant app) leaves the ambient tenant as it
/// was. The previous value is restored when the scope ends.
/// </summary>
internal static class BatchTenant
{
    /// <summary>Pins <paramref name="tenant"/> as the ambient tenant until disposed; nothing to pin when it is null.</summary>
    public static IDisposable Use(string? tenant) => tenant is null ? NoScope.Instance : new Scope(TenantId.Create(tenant));

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
