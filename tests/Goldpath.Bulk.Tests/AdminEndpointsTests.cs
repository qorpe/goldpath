using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>
/// The bulk upload route over real HTTP (TestServer), with the host's own exception handler and
/// problem details — the shape the nightly ZAP baseline found answering 500 for an unknown
/// definition (#377).
/// </summary>
public sealed class AdminEndpointsTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    public AdminEndpointsTests()
    {
        _connection.Open();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.Services.AddDbContext<BulkTestContext>(o => o.UseSqlite(_connection));
        builder.Services.AddScoped<IGoldpathBulkRowHandler<PaymentRow>, RecordingHandler>();
        builder.AddGoldpathBulk<WebApplicationBuilder, BulkTestContext>(bulk =>
        {
            bulk.AddBatch<PaymentRow>("payments", b => b.MaxRows(100).RowKey(r => r.EndToEndId));
            bulk.AddBatch<PaymentRow>("refunds", b => b.MaxRows(100).RowKey(r => r.EndToEndId));
        });

        _app = builder.Build();
        _app.UseExceptionHandler();
        _app.MapGoldpathBulkAdmin<BulkTestContext>(exposeUnsecured: true);
        using (var scope = _app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<BulkTestContext>().Database.EnsureCreated();
        }

        _app.Start();
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_definition_answers_404_naming_the_registered_definitions()
    {
        using var body = BulkFixture.Csv(("E2E-1", "DE89370400440532013000", "10.00", null));
        var response = await _client.PostAsync("/goldpath/admin/bulk/batches/paymnets", new StreamContent(body));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("No bulk definition named 'paymnets' — registered: payments, refunds.", problem.GetProperty("detail").GetString());

        using var scope = _app.Services.CreateScope();
        Assert.Equal(0, scope.ServiceProvider.GetRequiredService<BulkTestContext>().Set<GoldpathBulkBatch>().Count());   // nothing ingested
    }

    [Fact]
    public async Task Definition_names_match_exactly_as_the_engine_matches_them()
    {
        // The engine's lookup is ordinal; the route must not accept a spelling the engine would then refuse with a 500.
        using var body = BulkFixture.Csv(("E2E-1", "DE89370400440532013000", "10.00", null));
        var response = await _client.PostAsync("/goldpath/admin/bulk/batches/PAYMENTS", new StreamContent(body));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_known_definition_still_ingests_and_answers_the_batch()
    {
        using var body = BulkFixture.Csv(("E2E-1", "DE89370400440532013000", "10.00", null), ("E2E-2", "DE89370400440532013000", "20.00", null));
        var response = await _client.PostAsync("/goldpath/admin/bulk/batches/payments?fileName=july.csv", new StreamContent(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var batch = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("payments", batch.GetProperty("definition").GetString());
        Assert.False(string.IsNullOrEmpty(batch.GetProperty("state").GetString()));
        using var scope = _app.Services.CreateScope();
        Assert.Equal(1, scope.ServiceProvider.GetRequiredService<BulkTestContext>().Set<GoldpathBulkBatch>().Count(b => b.Definition == "payments"));
    }

    /// <summary>
    /// #476: a <c>?fileName=</c> that is given but blank is refused with 400 before the body is read, the same on every
    /// provider. It used to be stored as is — a nameless file on PostgreSQL, ORA-01400 (a 500) on Oracle, where '' is NULL.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("%20%20")]
    [InlineData("%09")]
    public async Task A_blank_file_name_is_refused_with_400_before_anything_is_stored(string blank)
    {
        using var body = BulkFixture.Csv(("E2E-1", "DE89370400440532013000", "10.00", null));
        var response = await _client.PostAsync($"/goldpath/admin/bulk/batches/payments?fileName={blank}", new StreamContent(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("fileName", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        Assert.Equal(0, db.Set<GoldpathBulkBatch>().Count());
        Assert.Equal(0, db.Set<GoldpathBulkFile>().Count());
    }

    [Fact]
    public async Task An_upload_without_a_file_name_is_stored_as_upload_csv()
    {
        using var body = BulkFixture.Csv(("E2E-1", "DE89370400440532013000", "10.00", null));
        var response = await _client.PostAsync("/goldpath/admin/bulk/batches/payments", new StreamContent(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _app.Services.CreateScope();
        Assert.Equal(["upload.csv"], scope.ServiceProvider.GetRequiredService<BulkTestContext>().Set<GoldpathBulkFile>().Select(f => f.FileName).ToList());
    }
}
