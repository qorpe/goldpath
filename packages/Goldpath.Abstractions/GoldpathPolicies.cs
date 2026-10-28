namespace Goldpath;

/// <summary>
/// Authorization policy names the asset agrees on across packages. The ADMIN surfaces
/// demand <see cref="Ops"/> OUT OF THE BOX (fail-closed — hardening H2); Goldpath.Auth
/// registers it from the manifest strategy, and the explicit opt-out on each mapper is
/// a VISIBLE decision, never a default.
/// </summary>
public static class GoldpathPolicies
{
    /// <summary>The ops-scoped policy every /goldpath/admin/* surface requires by default.</summary>
    public const string Ops = "goldpath-ops";

    /// <summary>
    /// The READ policy (admin-contract revision R4.1): every admin <c>GET</c> demands it, and every
    /// other method still demands <see cref="Ops"/>. Goldpath.Auth registers it so that the ops role
    /// satisfies it too, so an operator loses nothing and a viewer can read a surface without acting on it.
    /// </summary>
    public const string OpsRead = "goldpath-ops-read";

    /// <summary>
    /// The CROSS-TENANT ops policy (admin-contract revision R1): on a multi-tenant app,
    /// widening an admin read beyond the ambient tenant (an explicit `?tenant=` override
    /// or the all-tenants view) demands this on top of <see cref="Ops"/> — tenant
    /// isolation is the default, crossing it is a second, explicit privilege.
    /// </summary>
    public const string OpsAllTenants = "goldpath-ops-all-tenants";
}
