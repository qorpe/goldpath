using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// H8: the admin surface is the UI's FROZEN contract (docs/rfc/goldpath-admin-contract.md).
/// This test is the freeze's teeth on the campaign surface — a renamed or added route fails
/// here first, forcing the contract document into the same PR.
/// </summary>
public class RouteContractTests
{
    [Fact]
    public void The_campaign_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton(new GoldpathCampaignOptions());
        using var app = builder.Build();
        app.MapGoldpathCampaignAdmin<CampaignTestContext>();

        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order()
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/campaign/",
            "GET /goldpath/admin/campaign/{id:guid}",
            "GET /goldpath/admin/campaign/{id:guid}/audit",
            "GET /goldpath/admin/campaign/{id:guid}/failures",
            "GET /goldpath/admin/campaign/ceilings",
            "GET /goldpath/admin/campaign/ceilings/audit",
            "POST /goldpath/admin/campaign/",
            "POST /goldpath/admin/campaign/{id:guid}/abort",
            "POST /goldpath/admin/campaign/{id:guid}/pause",
            "POST /goldpath/admin/campaign/{id:guid}/resume",
            "POST /goldpath/admin/campaign/{id:guid}/throttle",
            "POST /goldpath/admin/campaign/ceilings/global/throttle",
            "POST /goldpath/admin/campaign/ceilings/target-systems/{name}/throttle",
        ];
        Assert.Equal(frozen, actual);
    }

    /// <summary>
    /// G4 API-9 (#398): the signed callback route, outside <c>/goldpath/admin</c>, frozen at its default prefix with the
    /// rest (admin contract, "Revision R5 — the signed callback route"). A target system is handed this path, so a
    /// rename strands every acceptance already in flight.
    /// </summary>
    [Fact]
    public void The_signed_callback_route_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton(new GoldpathCampaignOptions().AddCampaign<TestTarget>("device", c => c
            .MaxTargets(10).SignedCallbacks(CampaignFixture.CallbackSecret).Targets((_, _) => AsyncEnumerable.Empty<TestTarget>())));
        using var app = builder.Build();
        app.MapGoldpathCampaignCallbacks<CampaignTestContext>();

        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["POST /goldpath/campaign/callbacks/{type}/{campaignId:guid}/{correlationId}"], actual);
    }
}
