using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Archival.Tests;

/// <summary>
/// G4 API-9 (#398): the archival admin surface is part of the frozen admin contract
/// (<c>docs/rfc/goldpath-admin-contract.md</c>, its archival table). The exact, sorted <c>METHOD pattern</c> list is
/// compared with the frozen one, as <c>RouteContractTests</c> does for campaign: a renamed, added or removed route fails
/// here first, so the contract document changes in the same PR.
/// </summary>
public class ArchivalRouteContractTests
{
    [Fact]
    public void The_archival_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<ArchiveTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        using var app = builder.Build();
        app.MapGoldpathArchivalAdmin<ArchiveTestContext>(exposeUnsecured: true);

        // The builder's own data sources: the composite one in DI stays empty until routing is built.
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/archival/definitions",
            "GET /goldpath/admin/archival/entries/{definition}/{key}",
            "GET /goldpath/admin/archival/erasures",
            "GET /goldpath/admin/archival/holds",
            "POST /goldpath/admin/archival/definitions/{definition}/verify",
            "POST /goldpath/admin/archival/entries/{definition}/{key}/erase",
            "POST /goldpath/admin/archival/entries/{definition}/{key}/hold",
            "POST /goldpath/admin/archival/entries/{definition}/{key}/lift-hold",
        ];
        Assert.Equal(frozen, actual);
    }
}
