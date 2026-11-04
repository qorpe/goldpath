using System.Globalization;
using Goldpath;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.FileExchange.Tests;

/// <summary>
/// The database-backed ledger on a real database: the zero-duplicate guarantee SURVIVES the
/// engine instance — a "restarted" engine over the same database skips every already-applied
/// row, which is exactly what the in-memory ledger cannot promise. The facts are provider-neutral:
/// SQLite runs them here (<see cref="EfFileLedgerTests"/>), Oracle runs the same file in the
/// integration project's Oracle lane (G7, O-5b, #410).
/// </summary>
public abstract class EfFileLedgerFacts : IAsyncLifetime
{
    public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.AddGoldpathFileExchangeModel();
    }

    private sealed record Row(string Reference, decimal Amount);

    private readonly List<string> _applied = [];
    private ServiceProvider _provider = null!;

    /// <summary>Opens an empty database and returns the provider configuration that reaches it.</summary>
    protected abstract Task<Action<DbContextOptionsBuilder>> OpenDatabaseAsync();

    /// <summary>Releases what <see cref="OpenDatabaseAsync"/> opened.</summary>
    protected virtual Task CloseDatabaseAsync() => Task.CompletedTask;

    public async Task InitializeAsync()
    {
        var useProvider = await OpenDatabaseAsync();
        _provider = new ServiceCollection()
            .AddDbContext<LedgerDbContext>(useProvider)
            .BuildServiceProvider(true);
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await CloseDatabaseAsync();
    }

    private GoldpathFileRailEngine BuildEngine() => new(
        new GoldpathFileExchangeOptions().AddRail<Row>("registry-daily", rail => rail
            .Header(1)
            .ParseLine(line =>
            {
                var parts = line.Split(';');
                return new Row(parts[0], decimal.Parse(parts[1], CultureInfo.InvariantCulture));
            })
            .ValidateRow(row => row.Amount > 0 ? null : "non-positive amount")
            .Handle((row, _) =>
            {
                _applied.Add(row.Reference);
                return Task.CompletedTask;
            })),
        new GoldpathEfFileLedger<LedgerDbContext>(_provider.GetRequiredService<IServiceScopeFactory>()),
        NullLogger<GoldpathFileRailEngine>.Instance);

    [Fact]
    public async Task Zero_duplicates_survive_a_restart()
    {
        string[] file = ["H;2", "A-1;100.50", "A-2;200.00"];
        var first = BuildEngine();
        Assert.Equal(2, (await first.ProcessAsync("registry-daily", "reg.csv", file)).Processed);

        // A brand-new engine + ledger over the SAME database — the redelivery after restart.
        var restarted = BuildEngine();
        var replay = await restarted.ProcessAsync("registry-daily", "reg.csv", file);
        Assert.Equal(0, replay.Processed);
        Assert.Equal(2, replay.SkippedAsDuplicate);
        Assert.Equal(["A-1", "A-2"], _applied);
    }

    [Fact]
    public async Task The_archive_mark_is_idempotent_one_run_one_row()
    {
        var ledger = new GoldpathEfFileLedger<LedgerDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        await ledger.MarkArchivedAsync("registry-daily", "reg.csv");
        await ledger.MarkArchivedAsync("registry-daily", "reg.csv");   // the replay marks again

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        Assert.Equal(1, await db.Set<GoldpathFileArchiveRow>().CountAsync());
    }

    [Fact]
    public async Task A_requarantine_UPSERTS_the_reason_and_a_release_of_nothing_is_quiet()
    {
        var ledger = new GoldpathEfFileLedger<LedgerDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        await ledger.QuarantineAsync("registry-daily", "reg.csv", 4, "first words");
        await ledger.QuarantineAsync("registry-daily", "reg.csv", 4, "second words");

        var rows = await ledger.GetQuarantineAsync("registry-daily", "reg.csv");
        Assert.Equal("second words", Assert.Single(rows).Reason);

        await ledger.ReleaseQuarantineAsync("registry-daily", "reg.csv", 99);   // nothing there — no throw
        Assert.Single(await ledger.GetQuarantineAsync("registry-daily", "reg.csv"));
    }

    [Fact]
    public void The_ledger_model_shape_is_the_contract()
    {
        // Table names, composite keys and the bounded columns are shipped schema — a
        // mutant that renames a table or drops a key length is a broken adopter database.
        using var scope = _provider.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Model;

        var processed = model.FindEntityType(typeof(GoldpathFileProcessedRow))!;
        Assert.Equal("GoldpathFileProcessed", processed.GetTableName());
        Assert.Equal(["Rail", "File", "Line"], processed.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(128, processed.FindProperty("Rail")!.GetMaxLength());
        Assert.Equal(256, processed.FindProperty("File")!.GetMaxLength());

        var quarantine = model.FindEntityType(typeof(GoldpathFileQuarantineRow))!;
        Assert.Equal("GoldpathFileQuarantine", quarantine.GetTableName());
        Assert.Equal(1024, quarantine.FindProperty("Reason")!.GetMaxLength());

        var archive = model.FindEntityType(typeof(GoldpathFileArchiveRow))!;
        Assert.Equal("GoldpathFileArchive", archive.GetTableName());
        Assert.Equal(["Rail", "File"], archive.FindPrimaryKey()!.Properties.Select(p => p.Name));
    }

    [Fact]
    public async Task Quarantine_persists_with_its_reason_and_clears_on_reprocess()
    {
        string[] broken = ["H;2", "A-1;100.50", "A-2;-1"];
        var engine = BuildEngine();
        await engine.ProcessAsync("registry-daily", "reg2.csv", broken);

        var restarted = BuildEngine();
        var ledger = new GoldpathEfFileLedger<LedgerDbContext>(_provider.GetRequiredService<IServiceScopeFactory>());
        var quarantine = await ledger.GetQuarantineAsync("registry-daily", "reg2.csv");
        Assert.Equal([(3, "non-positive amount")], quarantine.Select(q => (q.Line, q.Reason)));

        string[] fixedFile = ["H;2", "A-1;100.50", "A-2;75.00"];
        var result = await restarted.ProcessAsync("registry-daily", "reg2.csv", fixedFile);
        Assert.Equal(1, result.Processed);
        Assert.Equal(1, result.SkippedAsDuplicate);
        Assert.Empty(await ledger.GetQuarantineAsync("registry-daily", "reg2.csv"));
    }
}
