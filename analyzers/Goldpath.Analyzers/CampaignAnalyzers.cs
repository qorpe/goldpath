using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Goldpath.Analyzers;

/// <summary>Shared matching for the campaign rules.</summary>
internal static class CampaignMatching
{
    internal static bool IsAddCampaign(SyntaxNodeAnalysisContext context, out GenericNameSyntax? generic)
    {
        generic = null;
        if (context.Node is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax g }
            || g.Identifier.ValueText != "AddCampaign")
        {
            return false;
        }

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method
            || method.ContainingType.ToDisplayString() != "Goldpath.GoldpathCampaignOptions")
        {
            return false;
        }

        generic = g;
        return true;
    }

    internal static bool ConfigureCalls(InvocationExpressionSyntax addCampaign, string methodName)
        => addCampaign.ArgumentList.Arguments.Count > 1
            && addCampaign.ArgumentList.Arguments[1].Expression.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var name } && name == methodName);

    internal static string TargetTypeName(GenericNameSyntax generic)
        => generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString() ?? "targets";

    internal static bool IsInsideItemHandler(SyntaxNodeAnalysisContext context, SyntaxNode node, out INamedTypeSymbol? handlerType)
    {
        handlerType = null;
        var declaringClass = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (declaringClass is null
            || context.SemanticModel.GetDeclaredSymbol(declaringClass, context.CancellationToken) is not INamedTypeSymbol type)
        {
            return false;
        }

        // R2.1: both execution hooks are item handlers for GP1702/GP1703 — the typed-result
        // one must not SaveChanges or bypass the notification seam any more than the R1 one.
        var handlerInterfaces = new[]
        {
            context.Compilation.GetTypeByMetadataName("Goldpath.IGoldpathCampaignItemHandler`1"),
            context.Compilation.GetTypeByMetadataName("Goldpath.IGoldpathCampaignActionHandler`1"),
        };
        if (!type.AllInterfaces.Any(i => handlerInterfaces.Any(handlerInterface =>
            handlerInterface is not null && SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, handlerInterface))))
        {
            return false;
        }

        handlerType = type;
        return true;
    }
}

/// <summary>
/// GP1701: <c>AddCampaign</c> whose configuration never calls <c>MaxTargets</c> — an
/// unbounded enumeration at L4 scale is an outage (the builder throws at runtime; this
/// rule says it at BUILD time).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CampaignCeilingAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.CampaignWithoutCeiling);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(static ctx =>
        {
            if (!CampaignMatching.IsAddCampaign(ctx, out var generic))
            {
                return;
            }

            if (!CampaignMatching.ConfigureCalls((InvocationExpressionSyntax)ctx.Node, "MaxTargets"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.CampaignWithoutCeiling, ctx.Node.GetLocation(), CampaignMatching.TargetTypeName(generic!)));
            }
        }, SyntaxKind.InvocationExpression);
    }
}

/// <summary>
/// GP1702: an item handler calling <c>SaveChanges</c>/<c>SaveChangesAsync</c> — outcomes
/// flow through the batching SINK (constraint 4); per-item saves at 30M-item scale melt
/// the database and fight the set-based flush semantics.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CampaignHandlerSaveAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.CampaignHandlerSavesPerItem);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(static ctx =>
        {
            if (ctx.Node is not InvocationExpressionSyntax invocation
                || invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SaveChanges" or "SaveChangesAsync" })
            {
                return;
            }

            if (CampaignMatching.IsInsideItemHandler(ctx, invocation, out var type))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.CampaignHandlerSavesPerItem, invocation.GetLocation(), type!.Name));
            }
        }, SyntaxKind.InvocationExpression);
    }
}

/// <summary>
/// GP1703: a campaign item handler constructing an SMTP client directly — the evidence
/// discipline for human-facing messages exists (Goldpath.Notification); bypassing it at
/// campaign scale should be a VISIBLE decision. Info on purpose: when Goldpath.Notification is
/// referenced, GP1601 already escalates the same construction to a warning.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CampaignNotificationBypassAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] SmtpClientTypes =
    [
        "MailKit.Net.Smtp.SmtpClient",
        "System.Net.Mail.SmtpClient",
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Descriptors.CampaignHandlerBypassesNotification);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static compilationContext =>
        {
            var smtpTypes = SmtpClientTypes
                .Select(compilationContext.Compilation.GetTypeByMetadataName)
                .Where(t => t is not null)
                .ToArray();
            if (smtpTypes.Length == 0)
            {
                return;
            }

            compilationContext.RegisterSyntaxNodeAction(ctx =>
            {
                var creation = (ObjectCreationExpressionSyntax)ctx.Node;
                if (ctx.SemanticModel.GetTypeInfo(creation, ctx.CancellationToken).Type is not INamedTypeSymbol type
                    || !smtpTypes.Any(smtp => SymbolEqualityComparer.Default.Equals(type, smtp)))
                {
                    return;
                }

                if (CampaignMatching.IsInsideItemHandler(ctx, creation, out var handler))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.CampaignHandlerBypassesNotification, creation.GetLocation(), handler!.Name));
                }
            }, SyntaxKind.ObjectCreationExpression);
        });
    }
}
