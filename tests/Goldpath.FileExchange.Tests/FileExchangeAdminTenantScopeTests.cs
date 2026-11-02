using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Goldpath;
using Goldpath.Testing.AdminScope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.FileExchange.Tests;

/// <summary>
/// Admin-contract R1 rule 5 on the file-exchange surface (#481). The ledger carries no
/// tenant column, so on a multi-tenant app the surface is cross-tenant by nature, and every
/// read demands <see cref="GoldpathPolicies.OpsAllTenants"/>. Without that privilege, a
/// tenant-A operator would read every rail's files and every quarantined row's reason. Driven
/// over real HTTP behind the REAL ops floor, with a quarantined row on the ledger so a served
/// read has something to leak.
/// </summary>
public sealed class FileExchangeAdminTenantScopeTests
{
    private const string Prefix = "/goldpath/admin/fileexchange";

    private static readonly string[] Reads =
    [
        $"{Prefix}/rails",
        $"{Prefix}/files?rail=registry-daily",
        $"{Prefix}/quarantine?rail=registry-daily&file=reg.csv",
    ];

    private sealed record Row(string Reference, decimal Amount);

    [Fact]
    public async Task A_tenant_scoped_operator_on_a_multi_tenant_app_is_refused_on_every_read()
    {
        await using var app = await StartAsync(multiTenant: true);
        var client = OperatorScheme.Client(app, allTenants: false);

        // Every route is asked before anything is asserted, so a red run names ALL the leaks.
        var leaks = new List<string>();
        foreach (var route in Reads)
        {
            var response = await client.GetAsync(route);
            var body = await response.Content.ReadAsStringAsync();
            // The refusal names the missing policy, and the quarantined row's reason never
            // leaves in it.
            if (response.StatusCode != HttpStatusCode.Forbidden
                || !body.Contains(GoldpathPolicies.OpsAllTenants, StringComparison.Ordinal)
                || body.Contains("non-positive amount", StringComparison.Ordinal))
            {
                leaks.Add($"{route} answered {(int)response.StatusCode}: {body}");
            }
        }

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    [Fact]
    public async Task An_all_tenants_operator_on_a_multi_tenant_app_is_served()
    {
        await using var app = await StartAsync(multiTenant: true);
        await AssertServedAsync(OperatorScheme.Client(app, allTenants: true));
    }

    [Fact]
    public async Task A_single_tenant_app_serves_the_same_operator_as_before()
    {
        await using var app = await StartAsync(multiTenant: false);
        await AssertServedAsync(OperatorScheme.Client(app, allTenants: false));
    }

    private static async Task AssertServedAsync(HttpClient client)
    {
        foreach (var route in Reads)
        {
            var response = await client.GetAsync(route);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{route} answered {(int)response.StatusCode}, not 200");
        }

        var quarantine = await client.GetFromJsonAsync<JsonElement>(Reads[2]);
        Assert.Equal("non-positive amount", Assert.Single(quarantine.EnumerateArray()).GetProperty("reason").GetString());
    }

    private static async Task<WebApplication> StartAsync(bool multiTenant)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.AddGoldpathFileExchange(files => files
            .AddRail<Row>("registry-daily", rail => rail
                .Header(1)
                .ParseLine(line => new Row(line.Split(';')[0], decimal.Parse(line.Split(';')[1], System.Globalization.CultureInfo.InvariantCulture)))
                .ValidateRow(row => row.Amount > 0 ? null : "non-positive amount")
                .Handle((_, _) => Task.CompletedTask)));
        OperatorScheme.Compose(builder.Services, multiTenant);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGoldpathFileExchangeAdmin();
        await app.StartAsync();

        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<GoldpathFileRailEngine>()
            .ProcessAsync("registry-daily", "reg.csv", ["header", "A;1", "B;0"]);
        return app;
    }
}
