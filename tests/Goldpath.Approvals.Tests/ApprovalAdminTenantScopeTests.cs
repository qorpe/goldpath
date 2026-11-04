using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Goldpath;
using Goldpath.Testing.AdminScope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// Admin-contract R1 rule 5 on the approvals surface (#481). Approval requests carry no
/// tenant column, so on a multi-tenant app the surface is cross-tenant by nature, and every
/// read and both decisions demand <see cref="GoldpathPolicies.OpsAllTenants"/>, as campaign's
/// do. Driven over real HTTP behind the REAL ops floor (the guard is not opted out). The
/// operator's identity and roles come from a test scheme: a tenant-scoped operator holds
/// <c>goldpath-ops</c>, and an all-tenants operator holds the cross-tenant role too.
/// </summary>
public sealed class ApprovalAdminTenantScopeTests
{
    private const string Prefix = "/goldpath/admin/approvals";

    [Fact]
    public async Task A_tenant_scoped_operator_on_a_multi_tenant_app_is_refused_on_every_route_and_decides_nothing()
    {
        await using var host = await Host.StartAsync(multiTenant: true);
        var request = await host.Engine.RequestAsync("credit-limit", "K-1", 500_000m, "maker");
        var client = host.Client(allTenants: false);

        // Every route is asked before anything is asserted, so a red run names ALL the leaks.
        var calls = new (string Route, HttpResponseMessage Response)[]
        {
            ("GET /requests", await client.GetAsync($"{Prefix}/requests")),
            ("GET /requests/{id}", await client.GetAsync($"{Prefix}/requests/{request.Id}")),
            ("POST approve", await client.PostAsJsonAsync($"{Prefix}/requests/{request.Id}/approve", new { role = "expert", reason = "fits" })),
            ("POST reject", await client.PostAsJsonAsync($"{Prefix}/requests/{request.Id}/reject", new { role = "expert", reason = "no" })),
        };

        var leaks = new List<string>();
        foreach (var (route, response) in calls)
        {
            var body = await response.Content.ReadAsStringAsync();
            var refused = response.StatusCode == HttpStatusCode.Forbidden
                && JsonDocument.Parse(body).RootElement is var envelope
                && !envelope.GetProperty("ok").GetBoolean()
                && envelope.GetProperty("message").GetString()!.Contains(GoldpathPolicies.OpsAllTenants, StringComparison.Ordinal);
            if (!refused)
            {
                leaks.Add($"{route} answered {(int)response.StatusCode}: {body}");
            }
        }

        // The refusal is not cosmetic: neither verb reached the engine.
        var status = (await host.Store.GetAsync(request.Id))!.Status;
        if (status != GoldpathApprovalStatus.Pending)
        {
            leaks.Add($"the request is {status}, not Pending");
        }

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    [Fact]
    public async Task An_all_tenants_operator_on_a_multi_tenant_app_is_served()
    {
        await using var host = await Host.StartAsync(multiTenant: true);
        var request = await host.Engine.RequestAsync("credit-limit", "K-2", 500_000m, "maker");
        var client = host.Client(allTenants: true);

        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"{Prefix}/requests")).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Prefix}/requests/{request.Id}")).StatusCode);

        var approve = await client.PostAsJsonAsync($"{Prefix}/requests/{request.Id}/approve", new { role = "expert", reason = "fits" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("Applied", (await approve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var other = await host.Engine.RequestAsync("credit-limit", "K-3", 500_000m, "maker");
        var reject = await client.PostAsJsonAsync($"{Prefix}/requests/{other.Id}/reject", new { role = "expert", reason = "collateral missing" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
    }

    [Fact]
    public async Task A_single_tenant_app_serves_the_same_operator_as_before()
    {
        await using var host = await Host.StartAsync(multiTenant: false);
        var request = await host.Engine.RequestAsync("credit-limit", "K-4", 500_000m, "maker");
        var client = host.Client(allTenants: false);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Prefix}/requests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Prefix}/requests/{request.Id}")).StatusCode);
        var approve = await client.PostAsJsonAsync($"{Prefix}/requests/{request.Id}/approve", new { role = "expert", reason = "fits" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var other = await host.Engine.RequestAsync("credit-limit", "K-5", 500_000m, "maker");
        var reject = await client.PostAsJsonAsync($"{Prefix}/requests/{other.Id}/reject", new { role = "expert", reason = "collateral missing" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
    }

    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public GoldpathApprovalEngine Engine => app.Services.GetRequiredService<GoldpathApprovalEngine>();

        public IGoldpathApprovalStore Store => app.Services.GetRequiredService<IGoldpathApprovalStore>();

        public static async Task<Host> StartAsync(bool multiTenant)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddLogging();
            builder.AddGoldpathApprovals(approvals => approvals
                .AddLadder("credit-limit", l => l
                    .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
                    .TopRung("general-manager", TimeSpan.FromHours(24))));
            OperatorScheme.Compose(builder.Services, multiTenant);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGoldpathApprovalsAdmin();
            await app.StartAsync();
            return new Host(app);
        }

        public HttpClient Client(bool allTenants) => OperatorScheme.Client(app, allTenants);

        public ValueTask DisposeAsync() => app.DisposeAsync();
    }
}
