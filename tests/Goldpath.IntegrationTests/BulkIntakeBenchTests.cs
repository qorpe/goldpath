using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Tests.Integration;

/// <summary>
/// #300 (scripts/bench-bulk.sh intake → ops/bulk-benchmarks.md): a 10M-row device list
/// through upload → validate → report on real PostgreSQL, with a row key, so the in-file
/// duplicate check sees every row. It records rows/s, the peak managed heap sampled during
/// the run, the process's peak working set and the database's size. Rows come from
/// <c>GOLDPATH_BENCH_BULK_INTAKE_ROWS</c> (default 10,000,000); every 100th row repeats an
/// earlier, already written device, so duplicates are found and confirmed throughout the file.
/// </summary>
[Trait("Category", "Bench")]
[Collection(BulkIntakeBenchCollection.Name)]
public sealed class BulkIntakeBenchTests : IAsyncLifetime
{
    /// <summary>One line of an uploaded device list.</summary>
    public sealed class DeviceRow
    {
        public string DeviceId { get; set; } = "";
    }

    private static readonly int Rows = int.TryParse(Environment.GetEnvironmentVariable("GOLDPATH_BENCH_BULK_INTAKE_ROWS"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows) && rows > 0 ? rows : 10_000_000;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly ITestOutputHelper _output;
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"goldpath-bulk-intake-{Guid.NewGuid():N}.csv");
    private ServiceProvider _services = null!;
    private GoldpathBulkEngine<BulkTests.BulkDb> _engine = null!;

    public BulkIntakeBenchTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var options = new GoldpathBulkOptions();
        options.AddBatch<DeviceRow>("devices", b => b
            .MaxRows(Rows)
            .RowKey(r => r.DeviceId));

        var services = new ServiceCollection();
        services.AddDbContext<BulkTests.BulkDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        services.AddSingleton(TimeProvider.System);
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BulkTests.BulkDb>().Database.EnsureCreatedAsync();

        _engine = new GoldpathBulkEngine<BulkTests.BulkDb>(
            options, new GoldpathBulkFileStore<BulkTests.BulkDb>(TimeProvider.System),
            TimeProvider.System, NullLogger<GoldpathBulkEngine<BulkTests.BulkDb>>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
        File.Delete(_file);
    }

    /// <summary>Writes the device list to disk: the upload streams it, as a real one would arrive.</summary>
    private int WriteDeviceList()
    {
        var repeats = 0;
        using var writer = new StreamWriter(_file, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 20);
        writer.Write("DeviceId\n");
        for (var row = 1; row <= Rows; row++)
        {
            // Every 100th row repeats the device of the row 4,999 above it: a row that carried its
            // own device, already written (InsertBatchSize is 1,000), so each confirmation reads
            // the stored row — the expensive path.
            var device = row % 100 == 0 && row > 4_999 ? row - 4_999 : row;
            repeats += device == row ? 0 : 1;
            writer.Write("dev-");
            writer.Write(device.ToString("D12", CultureInfo.InvariantCulture));
            writer.Write('\n');
        }

        return repeats;
    }

    [Fact]
    public async Task Bench_a_10M_row_device_list_uploaded_validated_and_reported()
    {
        var repeats = WriteDeviceList();
        var fileBytes = new FileInfo(_file).Length;

        using var sampling = new CancellationTokenSource();
        long peakHeap = 0;
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: false));
                try
                {
                    await Task.Delay(100, sampling.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var baselineHeap = GC.GetTotalMemory(forceFullCollection: true);
        var watch = Stopwatch.StartNew();
        Guid batchId;
        TimeSpan upload;
        using (var scope = _services.CreateScope())
        {
            await using var stream = File.OpenRead(_file);
            var (batch, _) = await _engine.IngestAsync(scope.ServiceProvider, "devices", stream, "devices.csv", null, CancellationToken.None);
            batchId = batch.Id;
            upload = watch.Elapsed;
            await _engine.ValidateBatchAsync(scope.ServiceProvider, batchId, CancellationToken.None);
        }

        watch.Stop();
        await sampling.CancelAsync();
        await sampler;

        using var check = _services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<BulkTests.BulkDb>();
        var validated = await db.Set<GoldpathBulkBatch>().AsNoTracking().SingleAsync(b => b.Id == batchId);
        var duplicateFindings = await db.Set<GoldpathBulkRowError>().AsNoTracking()
            .CountAsync(e => e.BatchId == batchId && e.Field == "(row key)");
        var databaseBytes = await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync();
        var validate = watch.Elapsed - upload;

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-BULK intake: {Rows:N0} rows ({fileBytes / 1_048_576.0:F0} MiB CSV), {repeats:N0} repeated devices, {Environment.ProcessorCount} logical CPUs"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-BULK intake: upload {upload.TotalSeconds:F1} s, validate {validate.TotalSeconds:F1} s, total {watch.Elapsed.TotalSeconds:F1} s = {Rows / watch.Elapsed.TotalSeconds:F0} rows/s"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-BULK intake: peak managed heap {peakHeap / 1_048_576.0:F0} MiB (baseline {baselineHeap / 1_048_576.0:F0} MiB), peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1_048_576.0:F0} MiB, database {databaseBytes / 1_048_576.0:F0} MiB"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"BENCH-BULK intake: valid {validated.ValidRows:N0}, invalid {validated.InvalidRows:N0}, duplicate findings {duplicateFindings:N0}"));

        // The report must be right before the numbers mean anything.
        Assert.Equal(GoldpathBulkBatchState.Validated, validated.State);
        Assert.Equal(Rows, validated.TotalRows);
        Assert.Equal(repeats, validated.InvalidRows);
        Assert.Equal(repeats, duplicateFindings);
        Assert.Equal(Rows - repeats, validated.ValidRows);
    }
}

/// <summary>
/// Runs <see cref="BulkIntakeBenchTests"/> ALONE: bench.yml runs every <c>Category=Bench</c>
/// class in one assembly run, and a 10M-row PostgreSQL load beside the H6 reference benches
/// would shift the numbers their <c>ops/*-benchmarks.md</c> tables compare against.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BulkIntakeBenchCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "bulk-intake-bench";
}
