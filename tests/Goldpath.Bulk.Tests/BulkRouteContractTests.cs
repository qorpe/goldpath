using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// G4 API-9 (#398): the bulk admin surface is part of the frozen admin contract
/// (<c>docs/rfc/goldpath-admin-contract.md</c>, its bulk table). The exact, sorted <c>METHOD pattern</c> list is
/// compared with the frozen one, as <c>RouteContractTests</c> does for campaign: a renamed, added or removed route fails
/// here first, so the contract document changes in the same PR.
/// </summary>
public class BulkRouteContractTests
{
    [Fact]
    public void The_bulk_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<BulkTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        using var app = builder.Build();
        app.MapGoldpathBulkAdmin<BulkTestContext>(exposeUnsecured: true);

        // The builder's own data sources: the composite one in DI stays empty until routing is built.
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/bulk/batches",
            "GET /goldpath/admin/bulk/batches/{batchId:guid}",
            "GET /goldpath/admin/bulk/batches/{batchId:guid}/errors",
            "GET /goldpath/admin/bulk/definitions",
            "POST /goldpath/admin/bulk/batches/{batchId:guid}/approve",
            "POST /goldpath/admin/bulk/batches/{batchId:guid}/reject",
            "POST /goldpath/admin/bulk/batches/{definition}",
        ];
        Assert.Equal(frozen, actual);
    }
}
