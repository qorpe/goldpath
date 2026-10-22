using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Tests;

public class IdempotencyTests
{
    private sealed class HeaderTenantContext(IHttpContextAccessor accessor) : ITenantContext
    {
        public TenantId? Current =>
            accessor.HttpContext?.Request.Headers.TryGetValue("X-Test-Tenant", out var value) == true
            && TenantId.TryCreate(value.ToString(), out var tenant)
                ? tenant
                : null;
    }

    private static int s_executionCounter;

    private static async Task<WebApplication> StartAppAsync(
        Action<GoldpathIdempotencyOptions>? configure = null,
        Func<Task>? gate = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ITenantContext, HeaderTenantContext>();
        builder.Services.AddProblemDetails();
        builder.AddGoldpathIdempotency(configure);

        var app = builder.Build();
        app.MapPost("/api/v1/orders", async (HttpContext _) =>
        {
            if (gate is not null)
            {
                await gate();
            }

            var execution = Interlocked.Increment(ref s_executionCounter);
            return Results.Ok(new { execution });
        });
        app.MapGet("/api/v1/orders", () => Results.Ok(new { listed = true }));
        app.MapPost("/api/v1/orders/created", () => Results.Created("/api/v1/orders/42", new { id = 42 }));
        app.MapPut("/api/v1/orders/7", () => Results.Ok(new { execution = Interlocked.Increment(ref s_executionCounter) }));
        app.MapPatch("/api/v1/orders/7", () => Results.Ok(new { execution = Interlocked.Increment(ref s_executionCounter) }));
        // Outside 2xx: a refusal and a 300 must never be stored, so a retry runs the handler again.
        app.MapPost("/api/v1/orders/refused", () => Results.Json(
            new { execution = Interlocked.Increment(ref s_executionCounter) }, statusCode: StatusCodes.Status409Conflict));
        app.MapPost("/api/v1/orders/choices", () => Results.Json(
            new { execution = Interlocked.Increment(ref s_executionCounter) }, statusCode: StatusCodes.Status300MultipleChoices));

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task A_replayed_created_keeps_its_location_header()
    {
        // Audit A7: the Location of a 201 is the one pointer the caller retried to get.
        using var app = await StartAppAsync();
        var client = app.GetTestClient();
        app.Lifetime.ApplicationStopping.Register(() => { });

        HttpRequestMessage Created(string key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/created")
            {
                Content = JsonContent.Create(new { name = "x" }),
            };
            request.Headers.Add(GoldpathHeaders.IdempotencyKey, key);
            return request;
        }

        var first = await client.SendAsync(Created("loc-1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("/api/v1/orders/42", first.Headers.Location!.ToString());

        var retry = await client.SendAsync(Created("loc-1"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues(GoldpathIdempotencyMiddleware.ReplayHeader).Single());
        Assert.Equal("/api/v1/orders/42", retry.Headers.Location!.ToString());   // survived the replay
    }

    private static HttpRequestMessage Request(string key, object body, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(GoldpathHeaders.IdempotencyKey, key);
        if (tenant is not null)
        {
            request.Headers.Add("X-Test-Tenant", tenant);
        }

        return request;
    }

    [Fact]
    public async Task Retry_with_same_key_replays_the_stored_response_byte_for_byte()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        var first = await client.SendAsync(Request("key-1", new { amount = 10 }));
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains(GoldpathIdempotencyMiddleware.ReplayHeader));

        var retry = await client.SendAsync(Request("key-1", new { amount = 10 }));
        var retryBody = await retry.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstBody, retryBody);                                        // handler did NOT run again
        Assert.Equal("true", retry.Headers.GetValues(GoldpathIdempotencyMiddleware.ReplayHeader).Single());
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected_with_422()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        await client.SendAsync(Request("key-2", new { amount = 10 }));
        var mismatch = await client.SendAsync(Request("key-2", new { amount = 999 }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("application/problem+json", mismatch.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Concurrent_same_key_gets_409_while_first_is_in_flight()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAppAsync(gate: () => gate.Task);
        var client = app.GetTestClient();

        var held = client.SendAsync(Request("key-3", new { amount = 1 }));
        await Task.Delay(300);                                                     // let it take the lock

        var concurrent = await client.SendAsync(Request("key-3", new { amount = 1 }));
        Assert.Equal(HttpStatusCode.Conflict, concurrent.StatusCode);

        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
    }

    [Fact]
    public async Task Different_tenants_with_the_same_key_are_isolated()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        var tenantA = await client.SendAsync(Request("key-4", new { amount = 1 }, tenant: "acme"));
        var tenantB = await client.SendAsync(Request("key-4", new { amount = 1 }, tenant: "globex"));

        Assert.False(tenantB.Headers.Contains(GoldpathIdempotencyMiddleware.ReplayHeader));   // B executed, no replay
        Assert.NotEqual(
            await tenantA.Content.ReadAsStringAsync(),
            await tenantB.Content.ReadAsStringAsync());                                  // separate executions
    }

    [Fact]
    public async Task Requests_without_the_header_and_GETs_pass_through()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        var first = await client.PostAsJsonAsync("/api/v1/orders", new { amount = 1 });
        var second = await client.PostAsJsonAsync("/api/v1/orders", new { amount = 1 });
        Assert.NotEqual(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());                                   // both executed

        var get = await client.GetAsync("/api/v1/orders");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task Put_and_patch_are_mutating_and_replay_like_post(string method)
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        HttpRequestMessage Send()
        {
            var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/orders/7") { Content = JsonContent.Create(new { amount = 1 }) };
            request.Headers.Add(GoldpathHeaders.IdempotencyKey, $"key-{method}");
            return request;
        }

        var first = await client.SendAsync(Send());
        var retry = await client.SendAsync(Send());

        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal("true", retry.Headers.GetValues(GoldpathIdempotencyMiddleware.ReplayHeader).Single());
    }

    [Theory]
    [InlineData("/api/v1/orders/refused")]   // 409: a refusal is not a result to replay
    [InlineData("/api/v1/orders/choices")]   // 300: the 2xx window ends at 299
    public async Task Responses_outside_2xx_are_not_stored_so_a_retry_runs_again(string path)
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();

        HttpRequestMessage Send()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { amount = 1 }) };
            request.Headers.Add(GoldpathHeaders.IdempotencyKey, "key-" + path);
            return request;
        }

        var first = await client.SendAsync(Send());
        var retry = await client.SendAsync(Send());

        Assert.False(retry.Headers.Contains(GoldpathIdempotencyMiddleware.ReplayHeader));
        Assert.NotEqual(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_problem_titles_say_which_misuse_it_was()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAppAsync(gate: () => gate.Task);
        var client = app.GetTestClient();

        var held = client.SendAsync(Request("key-titles", new { amount = 1 }));
        await Task.Delay(300);
        var inFlight = await client.SendAsync(Request("key-titles", new { amount = 1 }));
        Assert.Contains("A request with this Idempotency-Key is currently in flight.", await inFlight.Content.ReadAsStringAsync());

        gate.SetResult();
        await held;
        var mismatch = await client.SendAsync(Request("key-titles", new { amount = 2 }));
        Assert.Contains("This Idempotency-Key was already used with a different payload.", await mismatch.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wait_mode_serializes_and_replays_instead_of_409()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAppAsync(
            configure: o => o.OnConflict = IdempotencyConflictBehavior.Wait,
            gate: () => gate.Task);
        var client = app.GetTestClient();

        var held = client.SendAsync(Request("key-5", new { amount = 1 }));
        await Task.Delay(300);
        var waiting = client.SendAsync(Request("key-5", new { amount = 1 }));
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);                                        // genuinely waiting, not 409

        gate.SetResult();
        var heldResponse = await held;
        var waitedResponse = await waiting;

        Assert.Equal(HttpStatusCode.OK, waitedResponse.StatusCode);
        Assert.Equal(
            await heldResponse.Content.ReadAsStringAsync(),
            await waitedResponse.Content.ReadAsStringAsync());                    // replayed the first result
    }
}
