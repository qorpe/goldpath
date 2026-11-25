using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Goldpath;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Approvals.Tests;

/// <summary>
/// The admin surface over real HTTP (TestServer): routes, the R3 query shape, and the
/// frozen verb envelope — including the rule-name-as-message refusal the console prints
/// verbatim. Decisions run the ENGINE; the principal-less test host decides as
/// "anonymous", which four-eyes treats like any other identity. A request may carry <c>X-Test-User</c> and
/// <c>X-Test-Roles</c> (comma-separated), which become the principal's name and role claims — what an authentication
/// handler would put there — so the role rule (#478) is driven by the principal, as it is in production.
/// </summary>
public sealed class ApprovalAdminEndpointsTests : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly GoldpathApprovalEngine _engine;

    public ApprovalAdminEndpointsTests()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddLogging();
        builder.AddGoldpathApprovals(approvals => approvals
            .AddLadder("credit-limit", l => l
                .Rung("expert", 1_000_000m, TimeSpan.FromHours(8))
                .TopRung("general-manager", TimeSpan.FromHours(24))));

        _app = builder.Build();
        _app.Use((context, next) =>
        {
            var name = context.Request.Headers["X-Test-User"].ToString();
            var roles = context.Request.Headers["X-Test-Roles"].ToString();
            // A role under a claim type that is not the identity's role claim type ("roles", as some identity providers
            // send it): not a role to IsInRole, and so not one to the decision either.
            var otherClaim = context.Request.Headers["X-Test-Other-Claim"].ToString();
            if (name.Length > 0 || roles.Length > 0 || otherClaim.Length > 0)
            {
                var claims = new List<Claim>();
                if (name.Length > 0)
                {
                    claims.Add(new Claim(ClaimTypes.Name, name));
                }

                claims.AddRange(roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim(ClaimTypes.Role, role)));
                claims.AddRange(otherClaim.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(role => new Claim("roles", role)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            }

            return next(context);
        });
        _app.MapGoldpathApprovalsAdmin(exposeUnsecured: true);
        _app.Start();
        _client = _app.GetTestClient();
        _engine = _app.Services.GetRequiredService<GoldpathApprovalEngine>();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task The_list_answers_with_R3_filters_and_the_detail_carries_the_trail()
    {
        var pending = await _engine.RequestAsync("credit-limit", "K-1", 500_000m, "maker");
        var rejected = await _engine.RequestAsync("credit-limit", "K-2", 500_000m, "maker");
        await _engine.DecideAsync(rejected.Id, "checker", "expert", false, "collateral missing");

        var all = await _client.GetFromJsonAsync<JsonElement>("/goldpath/admin/approvals/requests?take=50");
        Assert.Equal(2, all.GetArrayLength());

        var filtered = await _client.GetFromJsonAsync<JsonElement>(
            "/goldpath/admin/approvals/requests?status=Pending&ladder=credit-limit&take=50");
        Assert.Equal(pending.Id.ToString(), Assert.Single(filtered.EnumerateArray()).GetProperty("id").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{rejected.Id}");
        Assert.Equal("Rejected", detail.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("trail").GetArrayLength());

        var missing = await _client.GetAsync($"/goldpath/admin/approvals/requests/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Approve_answers_the_frozen_envelope_and_the_engine_applied_it()
    {
        var request = await _engine.RequestAsync("credit-limit", "K-3", 500_000m, "maker");

        var response = await PostAsync(
            $"/goldpath/admin/approvals/requests/{request.Id}/approve",
            new { role = "expert", reason = "fits the limit" }, roles: "expert");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("Applied", envelope.GetProperty("message").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{request.Id}");
        Assert.Equal("Granted", detail.GetProperty("request").GetProperty("status").GetString());
        // The principal-less host decided as "anonymous" — the trail names it honestly.
        Assert.Equal("anonymous", detail.GetProperty("request").GetProperty("decidedBy").GetString());
    }

    [Fact]
    public async Task A_refusal_is_a_400_whose_message_is_the_rule_name()
    {
        var request = await _engine.RequestAsync("credit-limit", "K-4", 500_000m, "maker");

        // Blank rejection reason: the ENGINE's rule refuses, the envelope names it.
        var response = await PostAsync(
            $"/goldpath/admin/approvals/requests/{request.Id}/reject",
            new { role = "expert", reason = "" }, roles: "expert");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("ReasonRequired", envelope.GetProperty("message").GetString());

        // No reason at all (the field absent) is the same refusal, for a decider holding the rung's role.
        var absent = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/reject", new { role = "expert" }, roles: "expert");
        Assert.Equal("ReasonRequired", (await absent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        // A decision on an unknown id speaks through the SAME envelope — the engine's
        // NotFound outcome is a refusal message, not an HTTP 404 (that is the GET's job).
        var missing = await _client.PostAsJsonAsync(
            $"/goldpath/admin/approvals/requests/{Guid.NewGuid()}/approve", new { role = "expert" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var missingEnvelope = await missing.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("NotFound", missingEnvelope.GetProperty("message").GetString());
    }
    /// <summary>
    /// #478 (T-APR-03): the role in the body is an assertion, not a grant. A principal holding only the expert rung's
    /// role that names the top rung's role in the body is refused <c>WrongRole</c> on approve AND on reject, and the
    /// request stays pending with nothing on its trail; the same principal naming its own role on the top rung is
    /// refused too, because that is not the pending rung. Holding the top rung's role, it applies.
    /// </summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task A_role_claimed_in_the_body_but_not_held_by_the_principal_is_refused(string verb)
    {
        var request = await _engine.RequestAsync("credit-limit", "K-5", 5_000_000m, "maker");   // above the expert's limit: the top rung

        foreach (var (role, roles) in new[] { ("general-manager", "expert"), ("expert", "expert"), ("general-manager", "") })
        {
            var refused = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/{verb}",
                new { role, reason = "claimed" }, user: "checker", roles: roles);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("WrongRole", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        }

        var pending = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{request.Id}");
        Assert.Equal("Pending", pending.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(1, pending.GetProperty("trail").GetArrayLength());   // the request itself, and no decision

        var applied = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/{verb}",
            new { role = "general-manager", reason = "within mandate" }, user: "checker", roles: "expert,general-manager");
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var decided = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{request.Id}");
        Assert.Equal(verb == "approve" ? "Granted" : "Rejected", decided.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("checker", decided.GetProperty("request").GetProperty("decidedBy").GetString());
    }

    /// <summary>
    /// The class of "a role the principal does not hold", in every spelling the body can carry: a missing role, an empty
    /// or blank one, the held role in another case or with a trailing space, and a role held only under a claim type that
    /// is not the identity's role claim type (what the admin floor's <c>RequireRole</c> would not accept either). Each is
    /// <c>WrongRole</c>; nothing outside an exact role the principal holds counts.
    /// </summary>
    [Theory]
    [InlineData("{\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":null,\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":\"\",\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":\"   \",\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":\"Expert\",\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":\"expert \",\"reason\":\"x\"}", "expert")]
    [InlineData("{\"role\":\"expert\",\"reason\":\"x\"}", "roles:expert")]
    public async Task Every_spelling_of_a_role_not_held_is_refused(string body, string roles)
    {
        var request = await _engine.RequestAsync("credit-limit", $"K-{Guid.NewGuid():N}", 500_000m, "maker");
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/goldpath/admin/approvals/requests/{request.Id}/approve")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-Test-User", "checker");
        if (roles.StartsWith("roles:", StringComparison.Ordinal))
        {
            message.Headers.Add("X-Test-Other-Claim", roles["roles:".Length..]);
        }
        else
        {
            message.Headers.Add("X-Test-Roles", roles);
        }

        var response = await _client.SendAsync(message);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("WrongRole", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{request.Id}");
        Assert.Equal("Pending", detail.GetProperty("request").GetProperty("status").GetString());
    }

    /// <summary>
    /// A delegate decides through the admin surface by the delegation the engine holds for its identity, not by a role
    /// claim: it holds no role, names the delegated rung in the body, and applies; naming another rung, or none, is
    /// refused; and a principal with neither the role nor a delegation is refused.
    /// </summary>
    [Fact]
    public async Task A_delegate_without_the_role_decides_by_its_delegation_only()
    {
        await _engine.DelegateRoleAsync("expert-user", "expert", "stand-in", TimeSpan.FromHours(1));
        var request = await _engine.RequestAsync("credit-limit", "K-6", 500_000m, "maker");

        var stranger = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/approve",
            new { role = "expert", reason = "x" }, user: "stranger", roles: "");
        Assert.Equal("WrongRole", (await stranger.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        // The delegate's assertion must still be true: naming another rung's role, or none, is WrongRole although the
        // delegation would admit it to the pending rung (found driving the console host: the assertion was dropped).
        foreach (var wrong in new object[] { new { role = "general-manager", reason = "x" }, new { role = "", reason = "x" }, new { reason = "x" } })
        {
            var refused = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/approve", wrong, user: "stand-in", roles: "");
            Assert.Equal("WrongRole", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        }

        var delegate_ = await PostAsync($"/goldpath/admin/approvals/requests/{request.Id}/approve",
            new { role = "EXPERT", reason = "standing in" }, user: "stand-in", roles: "");   // the rung, in the engine's comparison
        Assert.Equal(HttpStatusCode.OK, delegate_.StatusCode);

        // A delegate's rejection carries its reason to the engine like anyone's: none is ReasonRequired, one applies and is
        // the reason on record.
        var other = await _engine.RequestAsync("credit-limit", "K-7", 500_000m, "maker");
        var bare = await PostAsync($"/goldpath/admin/approvals/requests/{other.Id}/reject", new { role = "expert" }, user: "stand-in", roles: "");
        Assert.Equal("ReasonRequired", (await bare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
        var reasoned = await PostAsync($"/goldpath/admin/approvals/requests/{other.Id}/reject",
            new { role = "expert", reason = "collateral missing" }, user: "stand-in", roles: "");
        Assert.Equal(HttpStatusCode.OK, reasoned.StatusCode);
        var rejected = await _client.GetFromJsonAsync<JsonElement>($"/goldpath/admin/approvals/requests/{other.Id}");
        Assert.Equal("collateral missing", rejected.GetProperty("request").GetProperty("reason").GetString());
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body, string? user = null, string roles = "")
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (user is not null)
        {
            message.Headers.Add("X-Test-User", user);
        }

        message.Headers.Add("X-Test-Roles", roles);
        return await _client.SendAsync(message);
    }
}
