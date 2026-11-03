using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Goldpath.Analyzers;

/// <summary>Shared name-based matching for batch-3 rules (no hard package dependencies).</summary>
internal static class Batch3Matching
{
    public static bool IsClassifiedProperty(IPropertySymbol property)
        => property.GetAttributes().Any(a => IsClassificationAttribute(a.AttributeClass));

    private static bool IsClassificationAttribute(INamedTypeSymbol? attribute)
    {
        for (var current = attribute; current is not null; current = current.BaseType)
        {
            if (current.Name is "GoldpathPersonalDataAttribute" or "GoldpathSensitiveDataAttribute"
                or "DataClassificationAttribute")
            {
                return true;
            }

            // Mediant's [SensitiveData] — by name, consistent with GP0402.
            if (current.Name == "SensitiveDataAttribute"
                && current.ContainingNamespace?.ToDisplayString().StartsWith("Mediant", StringComparison.Ordinal) is true)
            {
                return true;
            }
        }

        return false;
    }

    public static bool ImplementsMediantInterface(INamedTypeSymbol type, string interfaceName)
        => type.AllInterfaces.Any(i =>
            i.Name == interfaceName
            && i.ContainingNamespace?.ToDisplayString().StartsWith("Mediant", StringComparison.Ordinal) is true);

    public static bool HasAttributeNamed(INamedTypeSymbol type, string attributeName)
        => type.GetAttributes().Any(a => a.AttributeClass?.Name == attributeName);

    /// <summary>A raw key/name: a literal or an interpolated string fed straight into the call.</summary>
    public static bool IsRawStringArgument(IOperation value)
    {
        var current = value;
        while (current is IConversionOperation conversion)
        {
            current = conversion.Operand;
        }

        return current is ILiteralOperation { Type.SpecialType: SpecialType.System_String }
            or IInterpolatedStringOperation;
    }
}

/// <summary>
/// GP0701: a classified property on an integration event — PII crossing the service
/// boundary unmasked. GP0702: classification annotations in a compilation that does not
/// reference the DataProtection module — nothing will mask them anywhere.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClassificationBoundaryAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.ClassifiedOnIntegrationEvent, Descriptors.ClassifiedWithoutModule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var integrationEvent = start.Compilation.GetTypeByMetadataName("Goldpath.IIntegrationEvent");
            var moduleReferenced = start.Compilation.GetTypeByMetadataName("Goldpath.GoldpathDataProtectionExtensions") is not null;
            var orphaned = new ConcurrentBag<IPropertySymbol>();

            start.RegisterSymbolAction(ctx =>
            {
                var type = (INamedTypeSymbol)ctx.Symbol;
                foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!Batch3Matching.IsClassifiedProperty(property))
                    {
                        continue;
                    }

                    if (integrationEvent is not null
                        && type.AllInterfaces.Contains(integrationEvent, SymbolEqualityComparer.Default))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(
                            Descriptors.ClassifiedOnIntegrationEvent,
                            property.Locations[0], type.Name, property.Name));
                    }

                    if (!moduleReferenced)
                    {
                        orphaned.Add(property);
                    }
                }
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(ctx =>
            {
                foreach (var property in orphaned)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.ClassifiedWithoutModule,
                        property.Locations[0], property.ContainingType.Name, property.Name));
                }
            });
        });
    }
}

/// <summary>GP0801/GP0802: cache attributes on the wrong request kind (the GP1003 pattern).</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CacheAttributeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.CacheableOnCommand, Descriptors.InvalidatesCacheOnQuery);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(static ctx =>
        {
            var type = (INamedTypeSymbol)ctx.Symbol;
            if (Batch3Matching.ImplementsMediantInterface(type, "ICommand")
                && Batch3Matching.HasAttributeNamed(type, "CacheableAttribute"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.CacheableOnCommand, type.Locations[0], type.Name));
            }

            if (Batch3Matching.ImplementsMediantInterface(type, "IQuery")
                && Batch3Matching.HasAttributeNamed(type, "InvalidatesCacheAttribute"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.InvalidatesCacheOnQuery, type.Locations[0], type.Name));
            }
        }, SymbolKind.NamedType);
    }
}

/// <summary>
/// GP0803/GP1101: raw string keys/names fed straight into cache or lock APIs bypass the
/// tenant-scoped conventions. Only literals and interpolations are flagged — a variable may
/// well hold a convention-built value, and guessing would be noise.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RawKeyAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> s_cacheMethods =
        ImmutableHashSet.Create("GetOrCreateAsync", "SetAsync", "RemoveAsync");

    private static readonly ImmutableHashSet<string> s_lockMethods =
        ImmutableHashSet.Create("CreateLock", "AcquireLock", "TryAcquireLock", "AcquireLockAsync", "TryAcquireLockAsync");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.RawCacheKey, Descriptors.RawLockName);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(static ctx =>
        {
            var invocation = (IInvocationOperation)ctx.Operation;
            var method = invocation.TargetMethod;

            var isCache = s_cacheMethods.Contains(method.Name)
                && method.ContainingType?.Name == "HybridCache";
            var isLock = s_lockMethods.Contains(method.Name)
                && method.ContainingType?.ContainingNamespace?.ToDisplayString()
                    .StartsWith("Medallion.Threading", StringComparison.Ordinal) is true;
            if (!isCache && !isLock)
            {
                return;
            }

            var keyArgument = invocation.Arguments.FirstOrDefault(a =>
                a.Parameter is { Type.SpecialType: SpecialType.System_String, Name: "key" or "name" });
            if (keyArgument is not null && Batch3Matching.IsRawStringArgument(keyArgument.Value))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    isCache ? Descriptors.RawCacheKey : Descriptors.RawLockName,
                    keyArgument.Syntax.GetLocation()));
            }
        }, OperationKind.Invocation);
    }
}

/// <summary>
/// GP0902: application code writing TenantId on an IMultiTenant entity (the GP0502
/// pattern — the contributor stamps it; save contributors themselves are exempt).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ManualTenantWriteAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.ManualTenantWrite);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var marker = start.Compilation.GetTypeByMetadataName("Goldpath.IMultiTenant");
            var contributor = start.Compilation.GetTypeByMetadataName("Goldpath.IEntitySaveContributor");
            if (marker is null)
            {
                return;
            }

            start.RegisterOperationAction(ctx =>
            {
                var assignment = (ISimpleAssignmentOperation)ctx.Operation;
                if (assignment.Target is not IPropertyReferenceOperation { Property: var property }
                    || property.Name != "TenantId"
                    || property.ContainingType?.AllInterfaces.Contains(marker, SymbolEqualityComparer.Default) is not true)
                {
                    return;
                }

                var enclosing = ctx.ContainingSymbol.ContainingType;
                if (contributor is not null
                    && enclosing?.AllInterfaces.Contains(contributor, SymbolEqualityComparer.Default) is true)
                {
                    return;
                }

                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.ManualTenantWrite, assignment.Syntax.GetLocation(),
                    property.ContainingType.Name));
            }, OperationKind.SimpleAssignment);
        });
    }
}

/// <summary>
/// GP0903: IgnoreQueryFilters over an IMultiTenant entity with no GoldpathTenant.Bypass() in the
/// enclosing member — a SYNTACTIC heuristic (the GP0302 precedent), documented as such:
/// the scope may live in a caller, but filter-dodging next to no visible Bypass is exactly
/// the review flag this rule exists to raise.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IgnoreFiltersAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.IgnoreFiltersWithoutBypass);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            var marker = start.Compilation.GetTypeByMetadataName("Goldpath.IMultiTenant");
            if (marker is null)
            {
                return;
            }

            start.RegisterOperationAction(ctx =>
            {
                var invocation = (IInvocationOperation)ctx.Operation;
                if (invocation.TargetMethod.Name != "IgnoreQueryFilters"
                    || invocation.TargetMethod.TypeArguments.FirstOrDefault() is not INamedTypeSymbol entity
                    || !entity.AllInterfaces.Contains(marker, SymbolEqualityComparer.Default))
                {
                    return;
                }

                var enclosing = invocation.Syntax.Ancestors().FirstOrDefault(n =>
                    n is MethodDeclarationSyntax or LocalFunctionStatementSyntax
                        or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);
                if (enclosing?.ToString().Contains("Bypass") is not true)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.IgnoreFiltersWithoutBypass,
                        invocation.Syntax.GetLocation(), entity.Name));
                }
            }, OperationKind.Invocation);
        });
    }
}

/// <summary>
/// GP1102: a handle-returning acquire whose result is discarded, or stored without a
/// using — the lock leaks until the lease/session dies. Uncertain shapes (returned, passed
/// on) stay silent; this flags only what is provably wrong.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LockHandleAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableHashSet<string> s_acquireMethods = ImmutableHashSet.Create(
        "Acquire", "AcquireAsync", "TryAcquire", "TryAcquireAsync",
        "AcquireLock", "TryAcquireLock", "AcquireLockAsync", "TryAcquireLockAsync");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.LockHandleNotDisposed);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(static ctx =>
        {
            var invocation = (IInvocationOperation)ctx.Operation;
            if (!s_acquireMethods.Contains(invocation.TargetMethod.Name)
                || !ReturnsSynchronizationHandle(invocation.TargetMethod.ReturnType))
            {
                return;
            }

            var parent = invocation.Parent;
            while (parent is IConversionOperation or IAwaitOperation or IVariableInitializerOperation)
            {
                parent = parent.Parent;
            }

            var leaked = parent switch
            {
                IExpressionStatementOperation => true,                        // result discarded
                IVariableDeclaratorOperation declarator => !IsUnderUsing(declarator),
                _ => false,                                                   // returned/passed on: silent
            };

            if (leaked)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.LockHandleNotDisposed, invocation.Syntax.GetLocation()));
            }
        }, OperationKind.Invocation);
    }

    private static bool ReturnsSynchronizationHandle(ITypeSymbol returnType)
    {
        var unwrapped = returnType is INamedTypeSymbol { IsGenericType: true } generic
            && generic.Name is "Task" or "ValueTask"
                ? generic.TypeArguments[0]
                : returnType;
        return unwrapped.Name.Contains("SynchronizationHandle");
    }

    private static bool IsUnderUsing(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is IUsingDeclarationOperation or IUsingOperation)
            {
                return true;
            }

            if (current is IBlockOperation or IMethodBodyOperation)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// GP1201: every anonymous endpoint is inventory — the attribute and the fluent
/// AllowAnonymous() both count. GP1202: Goldpath auth/redaction secrets assigned from string
/// literals (HmacKey, ApiKeys entries, UseHmacRedaction) — secrets never live in source.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AuthSurfaceAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.AllowAnonymousInventory, Descriptors.SecretInSource);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSymbolAction(static ctx =>
        {
            if (ctx.Symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "AllowAnonymousAttribute"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.AllowAnonymousInventory, ctx.Symbol.Locations[0], ctx.Symbol.Name));
            }
        }, SymbolKind.Method, SymbolKind.NamedType);

        context.RegisterOperationAction(static ctx =>
        {
            var invocation = (IInvocationOperation)ctx.Operation;
            if (invocation.TargetMethod.Name == "AllowAnonymous")
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.AllowAnonymousInventory, invocation.Syntax.GetLocation(), "this endpoint"));
            }
            else if (invocation.TargetMethod.Name is "UseHmacRedaction" or "Add"
                && invocation.Arguments.Any(a => IsSecretArgument(invocation.TargetMethod.Name, a))
                && (invocation.TargetMethod.Name == "UseHmacRedaction"
                    || IsApiKeysMember(invocation.Instance)))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.SecretInSource, invocation.Syntax.GetLocation(),
                    invocation.TargetMethod.Name == "UseHmacRedaction" ? "UseHmacRedaction" : "ApiKeys"));
            }
        }, OperationKind.Invocation);

        context.RegisterOperationAction(static ctx =>
        {
            var assignment = (ISimpleAssignmentOperation)ctx.Operation;
            if (!Batch3Matching.IsRawStringArgument(assignment.Value))
            {
                return;
            }

            if (assignment.Target is IPropertyReferenceOperation { Property.Name: "HmacKey" })
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.SecretInSource, assignment.Syntax.GetLocation(), "HmacKey"));
            }
            else if (assignment.Target is IPropertyReferenceOperation { Property.IsIndexer: true } indexer
                && IsApiKeysMember(indexer.Instance))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.SecretInSource, assignment.Syntax.GetLocation(), "ApiKeys"));
            }
        }, OperationKind.SimpleAssignment);
    }

    private static bool IsApiKeysMember(IOperation? instance)
        => instance is IPropertyReferenceOperation { Property.Name: "ApiKeys" };

    // ApiKeys maps a client name to its key: on Add only the value (the key) is a secret, as on
    // the indexer form; a literal client name beside a key read from a store is not a finding.
    private static bool IsSecretArgument(string method, IArgumentOperation argument)
        => argument.Parameter is { Type.SpecialType: SpecialType.System_String } parameter
            && (method != "Add" || parameter.Ordinal == 1)
            && Batch3Matching.IsRawStringArgument(argument.Value);
}

/// <summary>
/// GP0904: an endpoint lambda that accepts a caller-supplied 'tenant' parameter without
/// consulting AdminTenantScope — the admin-contract R1 seam. Syntactic heuristic in the
/// GP0903 tradition: the lambda must mention AdminTenantScope somewhere in its body;
/// a Map* lambda taking tenant from the wire with no visible seam is exactly the
/// client-controlled fence the R1 revision exists to kill.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AdminTenantScopeAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.AdminEndpointSkipsTenantScope);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(static ctx =>
        {
            var lambda = (IAnonymousFunctionOperation)ctx.Operation;
            if (!lambda.Symbol.Parameters.Any(p =>
                    p.Name == "tenant" && p.Type.SpecialType == SpecialType.System_String))
            {
                return;
            }

            // Only endpoint lambdas: the enclosing invocation must be a Map* route mapper.
            if (lambda.Syntax.Ancestors().OfType<InvocationExpressionSyntax>().FirstOrDefault()
                    ?.Expression.ToString() is not { } target
                || !target.Contains(".Map", StringComparison.Ordinal))
            {
                return;
            }

            if (!lambda.Syntax.ToString().Contains("AdminTenantScope", StringComparison.Ordinal))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.AdminEndpointSkipsTenantScope, lambda.Syntax.GetLocation()));
            }
        }, OperationKind.AnonymousFunction);
    }
}
