using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Goldpath.Analyzers.Tests;

public class Batch3AnalyzerTests
{
    private const string Stubs = """
        namespace Goldpath
        {
            public interface IIntegrationEvent { }
            public interface IMultiTenant { }
            public interface IEntitySaveContributor { }
            public sealed class GoldpathPersonalDataAttribute : System.Attribute { }
            public static class GoldpathTenant { public static System.IDisposable Bypass() => null!; }
        }
        namespace Mediant.Abstractions
        {
            public interface ICommand<out T> { }
            public interface IQuery<out T> { }
        }
        namespace Mediant.Behaviors.Attributes
        {
            public sealed class CacheableAttribute : System.Attribute
            {
                public CacheableAttribute(int seconds) { }
            }
            public sealed class InvalidatesCacheAttribute : System.Attribute
            {
                public InvalidatesCacheAttribute(string prefix) { }
            }
        }
        namespace Microsoft.Extensions.Caching.Hybrid
        {
            public abstract class HybridCache
            {
                public System.Threading.Tasks.ValueTask SetAsync(string key, string value) => default;
            }
        }
        namespace Medallion.Threading
        {
            public interface IDistributedSynchronizationHandle : System.IDisposable { }
            public interface IDistributedLock
            {
                IDistributedSynchronizationHandle? TryAcquire(System.TimeSpan timeout = default);
            }
            public interface IDistributedLockProvider { IDistributedLock CreateLock(string name); }
        }
        namespace Microsoft.EntityFrameworkCore
        {
            public static class EfQueryableExtensions
            {
                public static System.Linq.IQueryable<T> IgnoreQueryFilters<T>(this System.Linq.IQueryable<T> q) => q;
            }
        }
        namespace Microsoft.AspNetCore.Authorization
        {
            public sealed class AllowAnonymousAttribute : System.Attribute { }
        }
        """;

    private static Task Verify<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source + "\n" + Stubs,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    // ── GP0701 / GP0702 ────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0701_flags_classified_property_on_an_integration_event()
        => Verify<ClassificationBoundaryAnalyzer>("""
            public record CustomerRegistered : Goldpath.IIntegrationEvent
            {
                [Goldpath.GoldpathPersonalData]
                public string? {|#0:NationalId|} { get; init; }
                public string? Segment { get; init; }
            }
            """,
            new DiagnosticResult(Descriptors.ClassifiedOnIntegrationEvent).WithLocation(0)
                .WithArguments("CustomerRegistered", "NationalId"),
            // No Goldpath.GoldpathDataProtectionExtensions in the stub compilation → 0702 fires too.
            new DiagnosticResult(Descriptors.ClassifiedWithoutModule).WithLocation(0)
                .WithArguments("CustomerRegistered", "NationalId"));

    [Fact]
    public Task GOLDPATH0702_quiet_when_the_module_is_referenced()
        => Verify<ClassificationBoundaryAnalyzer>("""
            public class Customer
            {
                [Goldpath.GoldpathPersonalData]
                public string? NationalId { get; set; }
            }
            namespace Goldpath { public static class GoldpathDataProtectionExtensions { } }
            """);

    /// <summary>
    /// Mediant's [SensitiveData] classifies a property too, through a derived attribute as well
    /// (the base chain is walked); a same-named attribute outside Mediant does not.
    /// </summary>
    [Fact]
    public Task GOLDPATH0701_honours_Mediants_SensitiveData_and_its_subclasses_only()
        => Verify<ClassificationBoundaryAnalyzer>("""
            public record CardIssued : Goldpath.IIntegrationEvent
            {
                [Mediant.Abstractions.SensitiveData]
                public string? {|#0:Pan|} { get; init; }
                [TaxId]
                public string? {|#1:TaxNumber|} { get; init; }
                [Other.SensitiveData]
                public string? Brand { get; init; }   // quiet: not Mediant's attribute
            }
            public sealed class TaxIdAttribute : Mediant.Abstractions.SensitiveDataAttribute { }
            namespace Mediant.Abstractions { public class SensitiveDataAttribute : System.Attribute { } }
            namespace Other { public sealed class SensitiveDataAttribute : System.Attribute { } }
            namespace Goldpath { public static class GoldpathDataProtectionExtensions { } }
            """,
            new DiagnosticResult(Descriptors.ClassifiedOnIntegrationEvent).WithLocation(0).WithArguments("CardIssued", "Pan"),
            new DiagnosticResult(Descriptors.ClassifiedOnIntegrationEvent).WithLocation(1).WithArguments("CardIssued", "TaxNumber"));

    // ── GP0801 / GP0802 ────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0801_flags_cacheable_command_and_GOLDPATH0802_flags_invalidating_query()
        => Verify<CacheAttributeAnalyzer>("""
            [Mediant.Behaviors.Attributes.Cacheable(300)]
            public record {|#0:ApproveLoan|} : Mediant.Abstractions.ICommand<string>;

            [Mediant.Behaviors.Attributes.InvalidatesCache("rates")]
            public record {|#1:GetRates|} : Mediant.Abstractions.IQuery<string>;

            [Mediant.Behaviors.Attributes.Cacheable(300)]
            public record GetLimits : Mediant.Abstractions.IQuery<string>;   // correct usage: quiet
            """,
            new DiagnosticResult(Descriptors.CacheableOnCommand).WithLocation(0).WithArguments("ApproveLoan"),
            new DiagnosticResult(Descriptors.InvalidatesCacheOnQuery).WithLocation(1).WithArguments("GetRates"));

    // ── GP0803 / GP1101 ────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0803_flags_raw_cache_keys_and_GOLDPATH1101_flags_raw_lock_names()
        => Verify<RawKeyAnalyzer>("""
            public class Service
            {
                public async System.Threading.Tasks.Task M(
                    Microsoft.Extensions.Caching.Hybrid.HybridCache cache,
                    Medallion.Threading.IDistributedLockProvider locks,
                    string builtElsewhere)
                {
                    await cache.SetAsync({|#0:"rates:current"|}, "v");
                    await cache.SetAsync(builtElsewhere, "v");            // variable: silent
                    locks.CreateLock({|#1:$"lock-{builtElsewhere}"|});
                    locks.CreateLock(builtElsewhere);                     // variable: silent
                }
            }
            """,
            new DiagnosticResult(Descriptors.RawCacheKey).WithLocation(0),
            new DiagnosticResult(Descriptors.RawLockName).WithLocation(1));

    // ── GP0902 ──────────────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0902_flags_app_writes_and_exempts_contributors()
        => Verify<ManualTenantWriteAnalyzer>("""
            public class Loan : Goldpath.IMultiTenant
            {
                public string? TenantId { get; set; }
            }
            public class Service
            {
                public void M(Loan loan) => {|#0:loan.TenantId = "acme"|};
            }
            public class Stamper : Goldpath.IEntitySaveContributor
            {
                public void Fill(Loan loan) => loan.TenantId = "infra";   // contributor: exempt
            }
            """,
            new DiagnosticResult(Descriptors.ManualTenantWrite).WithLocation(0).WithArguments("Loan"));

    // ── GP0903 ──────────────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0903_flags_filter_dodging_without_a_visible_bypass()
        => Verify<IgnoreFiltersAnalyzer>("""
            using Microsoft.EntityFrameworkCore;
            public class Loan : Goldpath.IMultiTenant { }
            public class Reports
            {
                public void Sneaky(System.Linq.IQueryable<Loan> loans)
                {
                    _ = {|#0:loans.IgnoreQueryFilters()|};
                }

                public void Honest(System.Linq.IQueryable<Loan> loans)
                {
                    using (Goldpath.GoldpathTenant.Bypass())
                    {
                        _ = loans.IgnoreQueryFilters();                   // scope visible: quiet
                    }
                }
            }
            """,
            new DiagnosticResult(Descriptors.IgnoreFiltersWithoutBypass).WithLocation(0).WithArguments("Loan"));

    // ── GP1102 ──────────────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH1102_flags_discarded_and_unusing_handles_only()
        => Verify<LockHandleAnalyzer>("""
            public class Jobs
            {
                public Medallion.Threading.IDistributedSynchronizationHandle? M(Medallion.Threading.IDistributedLock @lock)
                {
                    {|#0:@lock.TryAcquire()|};                                    // discarded: leak
                    var leaked = {|#1:@lock.TryAcquire()|};                       // stored without using: leak
                    using var held = @lock.TryAcquire();                          // quiet
                    return @lock.TryAcquire();                                    // returned: silent (caller owns)
                }
            }
            """,
            new DiagnosticResult(Descriptors.LockHandleNotDisposed).WithLocation(0),
            new DiagnosticResult(Descriptors.LockHandleNotDisposed).WithLocation(1));

    // ── GP1201 / GP1202 ────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH1201_inventories_the_anonymous_surface()
        => Verify<AuthSurfaceAnalyzer>("""
            public class PublicApi
            {
                [Microsoft.AspNetCore.Authorization.AllowAnonymous]
                public void {|#0:GetRates|}() { }
            }
            """,
            new DiagnosticResult(Descriptors.AllowAnonymousInventory).WithLocation(0).WithArguments("GetRates"));

    [Fact]
    public Task GOLDPATH1202_flags_literal_secrets_on_the_goldpath_surfaces()
        => Verify<AuthSurfaceAnalyzer>("""
            public class AuthOptions
            {
                public string? HmacKey { get; set; }
                public System.Collections.Generic.Dictionary<string, string> ApiKeys { get; } = new();
            }
            public class Setup
            {
                public void M(AuthOptions o, string fromSecretStore)
                {
                    {|#0:o.HmacKey = "hardcoded-key"|};
                    {|#1:o.ApiKeys["job"] = "hardcoded-key"|};
                    o.HmacKey = fromSecretStore;                          // quiet
                    o.ApiKeys["job"] = fromSecretStore;                   // quiet
                }
            }
            """,
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(0).WithArguments("HmacKey"),
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(1).WithArguments("ApiKeys"));

    [Fact]
    public Task GOLDPATH1201_inventories_the_fluent_AllowAnonymous_too()
        => Verify<AuthSurfaceAnalyzer>("""
            public sealed class Route
            {
                public Route AllowAnonymous() => this;
                public Route RequireAuthorization() => this;
            }
            public static class Endpoints
            {
                public static void Map(Route route)
                {
                    {|#0:route.AllowAnonymous()|};
                    route.RequireAuthorization();   // quiet
                }
            }
            """,
            new DiagnosticResult(Descriptors.AllowAnonymousInventory).WithLocation(0).WithArguments("this endpoint"));

    [Fact]
    public Task GOLDPATH1202_flags_literal_secrets_passed_to_UseHmacRedaction_and_ApiKeys_Add()
        => Verify<AuthSurfaceAnalyzer>("""
            public class DataProtectionOptions
            {
                public DataProtectionOptions UseHmacRedaction(string base64Key, int keyId = 1) => this;
            }
            public class AuthOptions
            {
                public System.Collections.Generic.Dictionary<string, string> ApiKeys { get; } = new();
                public System.Collections.Generic.Dictionary<string, string> Labels { get; } = new();
            }
            public class Setup
            {
                public void M(DataProtectionOptions dp, AuthOptions o, string fromSecretStore, string client)
                {
                    {|#0:dp.UseHmacRedaction("aGFyZGNvZGVk")|};
                    {|#1:dp.UseHmacRedaction($"{client}-key", 2)|};
                    {|#2:o.ApiKeys.Add(client, "hardcoded-key")|};
                    {|#3:o.ApiKeys.Add("job", "hardcoded-key")|};
                    dp.UseHmacRedaction(fromSecretStore);          // quiet
                    o.ApiKeys.Add(client, fromSecretStore);        // quiet
                    o.Labels.Add("job", "not-a-secret");           // quiet: not ApiKeys
                }
            }
            """,
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(0).WithArguments("UseHmacRedaction"),
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(1).WithArguments("UseHmacRedaction"),
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(2).WithArguments("ApiKeys"),
            new DiagnosticResult(Descriptors.SecretInSource).WithLocation(3).WithArguments("ApiKeys"));

    /// <summary>
    /// ApiKeys maps a client NAME to its key, so a literal client name beside a key read from a
    /// store is not a secret in source; the indexer form already reads it that way.
    /// </summary>
    [Fact]
    public Task GOLDPATH1202_quiet_for_a_literal_client_name_beside_a_stored_key()
        => Verify<AuthSurfaceAnalyzer>("""
            public class AuthOptions
            {
                public System.Collections.Generic.Dictionary<string, string> ApiKeys { get; } = new();
            }
            public class Setup
            {
                public void M(AuthOptions o, string fromSecretStore)
                {
                    o.ApiKeys.Add("job", fromSecretStore);
                    o.ApiKeys["job"] = fromSecretStore;
                }
            }
            """);
    // ── GP0904 ─────────────────────────────────────────────────────────────

    [Fact]
    public Task GOLDPATH0904_flags_a_map_lambda_taking_tenant_without_the_seam()
        => Verify<AdminTenantScopeAnalyzer>("""
            public static class Endpoints
            {
                public static void Map(Router app)
                    => app.MapGet("/batches", {|#0:(string? tenant, int take) => tenant + take|});
            }
            public sealed class Router
            {
                public void MapGet(string route, System.Func<string?, int, string> handler) { }
            }
            """,
            new DiagnosticResult(Descriptors.AdminEndpointSkipsTenantScope).WithLocation(0));

    [Fact]
    public Task GOLDPATH0904_quiet_when_the_lambda_resolves_the_seam()
        => Verify<AdminTenantScopeAnalyzer>("""
            public static class Endpoints
            {
                public static void Map(Router app)
                    => app.MapGet("/batches", (string? tenant, int take) => AdminTenantScope.Resolve(tenant) + take);
            }
            public sealed class Router
            {
                public void MapGet(string route, System.Func<string?, int, string> handler) { }
            }
            public static class AdminTenantScope
            {
                public static string Resolve(string? tenant) => tenant ?? "";
            }
            """);

    [Fact]
    public Task GOLDPATH0904_ignores_non_endpoint_lambdas()
        => Verify<AdminTenantScopeAnalyzer>("""
            public static class NotAnEndpoint
            {
                public static string Run()
                {
                    System.Func<string?, string> f = (string? tenant) => tenant ?? "";
                    return f("acme");
                }
            }
            """);
}
