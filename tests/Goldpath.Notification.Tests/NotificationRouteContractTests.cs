using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Notification.Tests;

/// <summary>
/// G4 API-9 (#398): the notification admin surface is part of the frozen admin contract
/// (<c>docs/rfc/goldpath-admin-contract.md</c>, its notification table). The exact, sorted <c>METHOD pattern</c> list is
/// compared with the frozen one, as <c>RouteContractTests</c> does for campaign: a renamed, added or removed route fails
/// here first, so the contract document changes in the same PR.
/// </summary>
public class NotificationRouteContractTests
{
    [Fact]
    public void The_notification_surface_matches_the_frozen_contract()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<NotificationTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        using var app = builder.Build();
        app.MapGoldpathNotificationAdmin<NotificationTestContext>(exposeUnsecured: true);

        // The builder's own data sources: the composite one in DI stays empty until routing is built.
        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] frozen =
        [
            "GET /goldpath/admin/notification/failures",
            "GET /goldpath/admin/notification/notifications",
            "GET /goldpath/admin/notification/notifications/{id:guid}",
            "GET /goldpath/admin/notification/suppressions",
            "GET /goldpath/admin/notification/templates",
        ];
        Assert.Equal(frozen, actual);
    }
}
