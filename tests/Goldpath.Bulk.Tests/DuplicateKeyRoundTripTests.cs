using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goldpath.Bulk.Tests;

/// <summary>A device row whose key the binder sets but the payload's JSON leaves out.</summary>
public sealed class IgnoredKeyRow
{
    [JsonIgnore]
    public string DeviceId { get; set; } = "";

    public string Label { get; set; } = "";
}

/// <summary>A device row whose key has a private setter: bound from the file, not deserialized.</summary>
public sealed class PrivateSetterKeyRow
{
    public string DeviceId { get; private set; } = "";

    public string Label { get; set; } = "";
}

/// <summary>A device row whose key survives the payload's round trip.</summary>
public sealed class PlainKeyRow
{
    public string DeviceId { get; set; } = "";

    public string Label { get; set; } = "";
}

/// <summary>
/// A device row whose key survives the payload only for SOME rows: a region of 0 is left out of the
/// JSON and comes back as the initializer's 5, so the first row's round-trip check passes.
/// </summary>
public sealed class RowDependentKeyRow
{
    public string DeviceId { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Region { get; set; } = 5;
}

/// <summary>Collects the engine's warnings, so a test can see which path a pass took.</summary>
public sealed class WarningLog<T> : ILogger<T>
{
    public List<string> Warnings { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Warnings.Add(formatter(state, exception));
        }
    }
}

/// <summary>
/// #300 review: the hash is confirmed by re-deriving the first row's key from its staged
/// payload. A key that does not survive the payload's JSON round trip must not turn a genuine
/// duplicate into a "collision" that is staged as valid — the report must read exactly as the
/// string table's did.
/// </summary>
public class DuplicateKeyRoundTripTests
{
    public static TheoryData<int> FlushSizes => new() { 1, 2, 1000 };

    [Theory]
    [MemberData(nameof(FlushSizes))]
    public async Task A_json_ignored_key_still_reports_every_repeat_against_its_first_row(int insertBatchSize)
        => AssertKeptInFull(await AssertReportAsync<IgnoredKeyRow>(r => r.DeviceId, insertBatchSize));

    [Theory]
    [MemberData(nameof(FlushSizes))]
    public async Task A_private_setter_key_still_reports_every_repeat_against_its_first_row(int insertBatchSize)
        => AssertKeptInFull(await AssertReportAsync<PrivateSetterKeyRow>(r => r.DeviceId, insertBatchSize));

    [Theory]
    [MemberData(nameof(FlushSizes))]
    public async Task A_key_that_survives_its_payload_keeps_hashes_and_warns_nothing(int insertBatchSize)
        => Assert.Empty(await AssertReportAsync<PlainKeyRow>(r => r.DeviceId, insertBatchSize));

    [Theory]
    [MemberData(nameof(FlushSizes))]
    public async Task A_key_lost_by_some_rows_only_reruns_the_pass_in_full_and_reports_as_the_string_table_did(int insertBatchSize)
    {
        // Row 1's key survives its payload, so the pass starts on hashes; row 3 repeats row 2,
        // whose payload gives back "B|5" for "B|0". The pass is taken again keeping keys in full.
        var (validated, rows, errors, warnings) = await ValidateAsync<RowDependentKeyRow>(
            r => r.DeviceId + "|" + r.Region, insertBatchSize, "DeviceId,Region\nA,3\nB,0\nB,0\nA,3\nC,0\n");

        Assert.Equal(GoldpathBulkBatchState.Validated, validated.State);
        Assert.Equal(5, validated.TotalRows);
        Assert.Equal(3, validated.ValidRows);
        Assert.Equal(2, validated.InvalidRows);
        Assert.Equal([1, 2, 5], rows);
        Assert.Equal(
            [
                (3, "duplicate of row 2 within the file"),
                (4, "duplicate of row 1 within the file"),
            ],
            errors);
        AssertKeptInFull(warnings);
    }

    private static void AssertKeptInFull(List<string> warnings)
    {
        var warning = Assert.Single(warnings);
        Assert.Contains("devices", warning, StringComparison.Ordinal);
        Assert.Contains("keeps every key in full", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hit_whose_payload_lost_the_key_is_refused_loudly_rather_than_read_as_a_collision()
    {
        // The first row's payload gives back "" or nothing: neither hashes to D1's hash, so the
        // payload lost the key, and treating the repeat as a collision would stage it as valid.
        var duplicates = new GoldpathBulkDuplicateKeys("devices");
        Assert.Null(await duplicates.FirstRowOfAsync("D1", 1, _ => throw new InvalidOperationException("never asked")));
        var lost = await Assert.ThrowsAsync<GoldpathBulkKeyNotReproducibleException>(
            async () => await duplicates.FirstRowOfAsync("D1", 2, _ => ValueTask.FromResult<string?>("")));
        Assert.Contains("'devices'", lost.Message, StringComparison.Ordinal);
        Assert.Contains("(row 1)", lost.Message, StringComparison.Ordinal);
        Assert.Contains("public, serialized properties", lost.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<GoldpathBulkKeyNotReproducibleException>(
            async () => await duplicates.FirstRowOfAsync("D1", 3, _ => ValueTask.FromResult<string?>(null)));

        // A genuine collision — the first row's key DOES hash to the hit's hash — is not refused.
        var colliding = new GoldpathBulkDuplicateKeys("devices", _ => 9);
        Assert.Null(await colliding.FirstRowOfAsync("D1", 1, _ => throw new InvalidOperationException("never asked")));
        Assert.Null(await colliding.FirstRowOfAsync("D2", 2, _ => ValueTask.FromResult<string?>("D1")));
    }

    [Fact]
    public async Task Keys_kept_in_full_never_ask_for_an_earlier_row_and_answer_the_first_one()
    {
        var duplicates = new GoldpathBulkDuplicateKeys("devices", _ => 1);
        duplicates.KeepKeysInFull();
        ValueTask<string?> NeverAsked(int row) => throw new InvalidOperationException($"asked for row {row}");

        Assert.Null(await duplicates.FirstRowOfAsync("D1", 1, NeverAsked));
        Assert.Null(await duplicates.FirstRowOfAsync("D2", 2, NeverAsked));
        Assert.Equal(1, await duplicates.FirstRowOfAsync("D1", 3, NeverAsked));
        Assert.Equal(1, await duplicates.FirstRowOfAsync("D1", 4, NeverAsked));
        Assert.Equal(2, await duplicates.FirstRowOfAsync("D2", 5, NeverAsked));
    }

    /// <summary>Runs a six-row file with three repeats through the engine; answers its warnings.</summary>
    private static async Task<List<string>> AssertReportAsync<TRow>(Func<TRow, string?> rowKey, int insertBatchSize)
        where TRow : class, new()
    {
        var (validated, rows, errors, warnings) = await ValidateAsync(
            rowKey, insertBatchSize, "DeviceId,Label\nD1,a\nD2,b\nD1,c\nD1,d\nD2,e\nD3,f\n");

        Assert.Equal(GoldpathBulkBatchState.Validated, validated.State);
        Assert.Equal(3, validated.ValidRows);
        Assert.Equal(3, validated.InvalidRows);
        Assert.Equal([1, 2, 6], rows);
        Assert.Equal(
            [
                (3, "duplicate of row 1 within the file"),
                (4, "duplicate of row 1 within the file"),
                (5, "duplicate of row 2 within the file"),
            ],
            errors);
        return warnings;
    }

    /// <summary>Ingests and validates <paramref name="csv"/>; answers the batch, its staged rows, its errors and the warnings.</summary>
    private static async Task<(GoldpathBulkBatch Batch, List<int> Rows, List<(int, string)> Errors, List<string> Warnings)> ValidateAsync<TRow>(
        Func<TRow, string?> rowKey, int insertBatchSize, string csv)
        where TRow : class, new()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new GoldpathBulkOptions { InsertBatchSize = insertBatchSize };
        options.AddBatch<TRow>("devices", b => b.MaxRows(100).RowKey(rowKey));

        var services = new ServiceCollection();
        services.AddDbContext<BulkTestContext>(o => o.UseSqlite(connection));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        db.Database.EnsureCreated();
        var log = new WarningLog<GoldpathBulkEngine<BulkTestContext>>();
        var engine = new GoldpathBulkEngine<BulkTestContext>(
            options, new GoldpathBulkFileStore<BulkTestContext>(TimeProvider.System), TimeProvider.System, log);

        var file = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var (batch, _) = await engine.IngestAsync(scope.ServiceProvider, "devices", file, "devices.csv", null, CancellationToken.None);
        await engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);

        db.ChangeTracker.Clear();
        return (
            db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id),
            db.Set<GoldpathBulkRow>().OrderBy(r => r.RowNumber).Select(r => r.RowNumber).ToList(),
            db.Set<GoldpathBulkRowError>().AsNoTracking().OrderBy(e => e.RowNumber).ToList().Select(e => (e.RowNumber, e.Message)).ToList(),
            log.Warnings);
    }
}
