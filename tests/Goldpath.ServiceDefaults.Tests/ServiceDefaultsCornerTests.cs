using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Trace;
using Xunit;

namespace Goldpath.Tests;

/// <summary>
/// The corners the first mutation run left alive (2026-09-03, 62%): the sampler ladder,
/// the correlation id's exact boundary, the live/ready tag split, the Development branch
/// of the pipeline, and the guard's queue.
/// </summary>
public class ServiceDefaultsCornerTests
{
    private static async Task<WebApplication> StartAppAsync(Action<GoldpathServiceDefaultsOptions>? configure = null, string environment = "Production", Action<WebApplicationBuilder>? services = null, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        services?.Invoke(builder);
        builder.AddGoldpathServiceDefaults(configure);
        var app = builder.Build();
        app.MapGoldpathDefaultEndpoints();
        app.MapGet("/ok", () => "ok");
        app.MapGet("/boom", () => { throw new InvalidOperationException("secret internal detail"); });
        map?.Invoke(app);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public void The_sampler_ladder_follows_the_profile_the_ratio_and_the_environment()
    {
        var standard = new GoldpathServiceDefaultsOptions();
        Assert.IsType<AlwaysOnSampler>(GoldpathServiceDefaultsExtensions.CreateSampler(standard, isDevelopment: true));   // RFC D4: dev samples everything
        Assert.Contains("0.1", GoldpathServiceDefaultsExtensions.CreateSampler(standard, isDevelopment: false).Description);

        var minimal = new GoldpathServiceDefaultsOptions();
        minimal.Observability.Profile = ObservabilityProfile.Minimal;
        Assert.Contains("0.01", GoldpathServiceDefaultsExtensions.CreateSampler(minimal, isDevelopment: false).Description);

        var full = new GoldpathServiceDefaultsOptions();
        full.Observability.Profile = ObservabilityProfile.Full;
        Assert.IsType<AlwaysOnSampler>(GoldpathServiceDefaultsExtensions.CreateSampler(full, isDevelopment: false));

        var explicitRatio = new GoldpathServiceDefaultsOptions();
        explicitRatio.Observability.Profile = ObservabilityProfile.Full;   // the ratio beats the profile
        explicitRatio.Observability.SamplingRatio = 0.25;
        var sampler = GoldpathServiceDefaultsExtensions.CreateSampler(explicitRatio, isDevelopment: false);
        Assert.IsType<ParentBasedSampler>(sampler);
        Assert.Contains("0.25", sampler.Description);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task The_inbound_correlation_id_length_boundary_is_exactly_128(int length, bool honored)
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        var id = new string('x', length);
        client.DefaultRequestHeaders.Add(GoldpathHeaders.CorrelationId, id);

        var echoed = (await client.GetAsync("/ok")).Headers.GetValues(GoldpathHeaders.CorrelationId).Single();

        Assert.Equal(honored, echoed == id);
    }

    [Theory]
    [InlineData("a-b_c.d:e9", true)]
    [InlineData("has space", false)]
    [InlineData("tab\there", false)]
    [InlineData("ünïcode", false)]
    public async Task The_inbound_correlation_id_alphabet_is_token_safe(string id, bool honored)
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(GoldpathHeaders.CorrelationId, id);

        var echoed = (await client.GetAsync("/ok")).Headers.GetValues(GoldpathHeaders.CorrelationId).Single();

        Assert.Equal(honored, echoed == id);
    }

    [Fact]
    public async Task The_correlation_id_reaches_the_items_bag_and_the_activity_tag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await StartAppAsync(map: a => a.MapGet("/where", (HttpContext http) =>
            $"{http.Items[CorrelationMiddleware.ItemKey]}|{Activity.Current?.GetTagItem("goldpath.correlation_id")}"));
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(GoldpathHeaders.CorrelationId, "corr-9");

        Assert.Equal("corr-9|corr-9", await client.GetStringAsync("/where"));
    }

    [Fact]
    public async Task Live_ignores_a_failing_readiness_check_and_ready_reports_it()
    {
        await using var app = await StartAppAsync(services: b =>
            b.Services.AddHealthChecks().AddCheck("database", () => HealthCheckResult.Unhealthy("down")));   // no "live" tag
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Development_shows_the_exception_where_production_hides_it()
    {
        // The exception HANDLER is a production posture: Development keeps the developer
        // page, whose problem details carry the exception itself — the stack you want on
        // your machine and never on a customer's.
        await using var app = await StartAppAsync(environment: "Development");
        var body = await (await app.GetTestClient().GetAsync("/boom")).Content.ReadAsStringAsync();

        Assert.Contains("secret internal detail", body);
    }

    [Fact]
    public async Task The_guard_queues_up_to_the_queue_limit_before_refusing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAppAsync(
            configure: o =>
            {
                o.RateLimiting.ConcurrencyLimit = 1;
                o.RateLimiting.QueueLimit = 1;
            },
            map: a => a.MapGet("/slow", async () =>
            {
                await gate.Task;
                return "done";
            }));
        var client = app.GetTestClient();

        var held = client.GetAsync("/slow");
        await Task.Delay(200);
        var queued = client.GetAsync("/ok");          // takes the ONE queue slot
        await Task.Delay(200);
        var refused = await client.GetAsync("/ok");   // nothing left: 429

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        gate.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await held).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);   // the queued one ran once the permit freed
    }

    [Fact]
    public async Task Problem_details_carry_the_current_activity_id_as_traceId()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        string? activityId = null;
        await using var app = await StartAppAsync(map: a => a.MapGet("/boom-traced", () =>
        {
            activityId = Activity.Current?.Id;
            throw new InvalidOperationException("secret");
        }));

        var body = await (await app.GetTestClient().GetAsync("/boom-traced")).Content.ReadAsStringAsync();

        Assert.NotNull(activityId);
        Assert.Contains($"\"traceId\":\"{activityId}\"", body);   // the W3C id, not the connection's TraceIdentifier
    }

    [Fact]
    public async Task A_generated_correlation_id_is_the_trace_id_when_a_trace_exists()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await StartAppAsync(map: a => a.MapGet("/trace", () => Activity.Current?.TraceId.ToString() ?? "<none>"));

        var response = await app.GetTestClient().GetAsync("/trace");
        var traceId = await response.Content.ReadAsStringAsync();

        Assert.NotEqual("<none>", traceId);
        Assert.Equal(traceId, response.Headers.GetValues(GoldpathHeaders.CorrelationId).Single());   // logs and traces join on one id
    }

    [Fact]
    public async Task Exported_logs_carry_the_formatted_message_and_the_scopes()
    {
        // Scopes are how the correlation id reaches an exported log record; without them the
        // OTLP log line has no id to join on.
        await using var app = await StartAppAsync();
        var logging = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<OpenTelemetry.Logs.OpenTelemetryLoggerOptions>>().CurrentValue;

        Assert.True(logging.IncludeScopes);
        Assert.True(logging.IncludeFormattedMessage);
    }

    [Fact]
    public async Task Live_reports_a_failing_check_that_is_tagged_live()
    {
        await using var app = await StartAppAsync(services: b =>
            b.Services.AddHealthChecks().AddCheck("deadlock", () => HealthCheckResult.Unhealthy("stuck"), tags: ["live"]));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.GetTestClient().GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task The_guard_s_refusal_says_why_in_its_title()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAppAsync(
            configure: o =>
            {
                o.RateLimiting.ConcurrencyLimit = 1;
                o.RateLimiting.QueueLimit = 0;
            },
            map: a => a.MapGet("/held", async () => await gate.Task));
        var client = app.GetTestClient();
        var held = client.GetAsync("/held");
        await Task.Delay(200);

        var rejected = await client.GetAsync("/ok");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Contains("\"title\":\"Too many concurrent requests.\"", await rejected.Content.ReadAsStringAsync());
        gate.SetResult();
        await held;
    }

    [Fact]
    public async Task Problem_details_carry_the_correlation_id_the_middleware_chose()
    {
        await using var app = await StartAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(GoldpathHeaders.CorrelationId, "corr-boom");

        var body = await (await client.GetAsync("/boom")).Content.ReadAsStringAsync();

        Assert.Contains("\"correlationId\":\"corr-boom\"", body);
    }
}
