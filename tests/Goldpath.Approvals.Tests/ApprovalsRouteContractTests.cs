using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// G4 API-9 (#398): the approvals admin surface is part of the frozen admin contract
/// (<c>docs/rfc/goldpath-admin-contract.md</c>, its approvals table). The exact, sorted <c>METHOD pattern</c> list is
/// compared with the frozen one, as <c>RouteContractTests</c> does for campaign: a renamed, added or removed route fails
/// here first, so the contract document changes in the same PR.
/// </summary>
public class ApprovalsRouteContractTests
{
    [Fact]
    public void The_approvals_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapGoldpathApprovalsAdmin(exposeUnsecured: true);

        // The builder's own data sources: the composite one in DI stays empty until routing is built.
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/approvals/requests",
            "GET /goldpath/admin/approvals/requests/{id:guid}",
            "POST /goldpath/admin/approvals/requests/{id:guid}/approve",
            "POST /goldpath/admin/approvals/requests/{id:guid}/reject",
        ];
        Assert.Equal(frozen, actual);
    }
}
