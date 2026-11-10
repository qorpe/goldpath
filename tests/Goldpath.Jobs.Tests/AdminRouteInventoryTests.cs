using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Jobs.Tests;

/// <summary>
/// What the jobs admin surface offers, and — the point of this file — what it REFUSES to
/// offer. ADR-0001 keeps job definitions in the manifest and the code; a screen that could
/// create one would put production behaviour outside every review. That rule has lived in
/// prose since the constitution was written. Here it is a test: if someone maps a
/// job-authoring route, this goes red, and no amount of documentation has to be trusted.
/// </summary>
public class AdminRouteInventoryTests
{
    /// <summary>
    /// G4 API-9 (#398): the exact route list, frozen. The facts below prove what the surface refuses to offer; this one
    /// proves nothing else moved — a renamed, added or removed route fails here, so the contract document
    /// (<c>docs/rfc/goldpath-admin-contract.md</c>, the jobs table and Revision R2) changes in the same PR.
    /// </summary>
    [Fact]
    public void The_jobs_surface_matches_the_frozen_contract()
    {
        string[] frozen =
        [
            "DELETE /goldpath/admin/jobs/fleets/{fleet}/calendars/{name}",
            "DELETE /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/triggers/{name}",
            "GET /goldpath/admin/jobs/audit",
            "GET /goldpath/admin/jobs/fleets",
            "GET /goldpath/admin/jobs/fleets/{fleet}/calendars",
            "GET /goldpath/admin/jobs/fleets/{fleet}/jobs",
            "GET /goldpath/admin/jobs/fleets/{fleet}/runs",
            "GET /goldpath/admin/jobs/fleets/{fleet}/status",
            "GET /goldpath/admin/jobs/runs/{runId:guid}",
            "POST /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/pause",
            "POST /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/reschedule",
            "POST /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/resume",
            "POST /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/trigger",
            "POST /goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/triggers",
            "POST /goldpath/admin/jobs/fleets/{fleet}/pause-all",
            "POST /goldpath/admin/jobs/fleets/{fleet}/resume-all",
            "POST /goldpath/admin/jobs/runs/{runId:guid}/replay-items",
            "POST /goldpath/admin/jobs/runs/{runId:guid}/rerun",
            "PUT /goldpath/admin/jobs/fleets/{fleet}/calendars/{name}",
        ];
        Assert.Equal(frozen, Inventory().Select(route => $"{route.Method} {route.Pattern}").Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void The_scheduling_routes_of_R2_are_mapped()
    {
        var routes = Inventory();

        Assert.Contains(("GET", "/goldpath/admin/jobs/fleets/{fleet}/status"), routes);
        Assert.Contains(("POST", "/goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/triggers"), routes);
        Assert.Contains(("DELETE", "/goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/triggers/{name}"), routes);
        // The frozen verbs T13 found unscreened are still here — the console reaching them
        // is U5's job, and this pins that they exist to be reached.
        Assert.Contains(("POST", "/goldpath/admin/jobs/fleets/{fleet}/pause-all"), routes);
        Assert.Contains(("POST", "/goldpath/admin/jobs/fleets/{fleet}/resume-all"), routes);
        Assert.Contains(("POST", "/goldpath/admin/jobs/fleets/{fleet}/jobs/{job}/reschedule"), routes);
        Assert.Contains(("GET", "/goldpath/admin/jobs/audit"), routes);
    }

    [Fact]
    public void NOTHING_here_can_bring_a_job_into_existence_or_remove_one()
    {
        var routes = Inventory();

        // Creating a job: a POST or PUT that terminates at the job collection or at a job
        // itself, rather than at one of its verbs.
        Assert.DoesNotContain(routes, route =>
            route.Method is "POST" or "PUT"
            && (route.Pattern.EndsWith("/jobs", StringComparison.Ordinal)
                || route.Pattern.EndsWith("/jobs/{job}", StringComparison.Ordinal)));

        // Deleting one: the only DELETE on this surface is scheduling (triggers, calendars).
        Assert.DoesNotContain(routes, route =>
            route.Method == "DELETE" && route.Pattern.EndsWith("/jobs/{job}", StringComparison.Ordinal));

        // And no picker to feed such a screen: reflecting the assembly's job types into a
        // list is the first half of the feature we are refusing.
        Assert.DoesNotContain(routes, route => route.Pattern.Contains("classes", StringComparison.OrdinalIgnoreCase));

        // Every DELETE that DOES exist is scheduling, named so the intent cannot drift.
        Assert.All(
            routes.Where(route => route.Method == "DELETE"),
            route => Assert.True(
                route.Pattern.Contains("/triggers/", StringComparison.Ordinal) || route.Pattern.Contains("/calendars/", StringComparison.Ordinal),
                $"unexpected DELETE on the jobs surface: {route.Pattern}"));
    }

    [Fact]
    public void The_data_map_is_readable_and_has_no_route_that_writes_it()
    {
        var routes = Inventory();

        // R2.6: diagnosis sees the parameters a run was given; changing them at runtime is
        // the same drift as authoring a job, only quieter.
        Assert.DoesNotContain(routes, route => route.Pattern.Contains("data", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_inventory_itself_is_not_empty()
    {
        // Every refusal above is a "does not contain": read an empty route table and they
        // all pass while proving nothing. This is the test that keeps them honest — it
        // caught exactly that, the first time this file ran.
        Assert.NotEmpty(Inventory());
    }

    private static List<(string Method, string Pattern)> Inventory()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddDbContext<JobsTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build();
        app.MapGoldpathJobsAdmin<JobsTestContext>(exposeUnsecured: true);

        // The builder's OWN data sources: the composite one in DI is not populated until
        // the routing middleware is built, which would make this read an empty table and
        // pass every "does not contain" assertion below for the wrong reason.
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                    .Select(method => (method, endpoint.RoutePattern.RawText ?? "")))
            .ToList();
    }
}
