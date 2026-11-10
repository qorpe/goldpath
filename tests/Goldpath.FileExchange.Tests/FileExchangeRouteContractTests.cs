using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.FileExchange.Tests;

/// <summary>
/// G4 API-9 (#398): the fileexchange admin surface is part of the frozen admin contract
/// (<c>docs/rfc/goldpath-admin-contract.md</c>, its fileexchange table). The exact, sorted <c>METHOD pattern</c> list is
/// compared with the frozen one, as <c>RouteContractTests</c> does for campaign: a renamed, added or removed route fails
/// here first, so the contract document changes in the same PR.
/// </summary>
public class FileExchangeRouteContractTests
{
    [Fact]
    public void The_fileexchange_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.AddGoldpathFileExchange(files => files.AddRail<string>("daily", rail => rail.ParseLine(line => line).Handle((_, _) => Task.CompletedTask)));
        using var app = builder.Build();
        app.MapGoldpathFileExchangeAdmin(exposeUnsecured: true);

        // The builder's own data sources: the composite one in DI stays empty until routing is built.
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/fileexchange/files",
            "GET /goldpath/admin/fileexchange/quarantine",
            "GET /goldpath/admin/fileexchange/rails",
        ];
        Assert.Equal(frozen, actual);
    }
}
