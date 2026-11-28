using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Goldpath.Bulk.Tests;

/// <summary>The finance card's row shape: a batch payment instruction.</summary>
public sealed class PaymentRow
{
    public string EndToEndId { get; set; } = "";
    public string Iban { get; set; } = "";
    public decimal Amount { get; set; }
    public string? Note { get; set; }
}

/// <summary>Records executions; scripts failures by row content ("FAIL" notes throw).</summary>
public sealed class RecordingHandler : IGoldpathBulkRowHandler<PaymentRow>
{
    public List<(PaymentRow Row, GoldpathBulkRowContext Context)> Executed { get; } = [];

    /// <summary>Runs first on every invocation — lets a test look at the world the handler sees.</summary>
    public Action<PaymentRow, GoldpathBulkRowContext>? Probe { get; set; }

    public Task ExecuteAsync(PaymentRow row, GoldpathBulkRowContext context, CancellationToken cancellationToken)
    {
        Probe?.Invoke(row, context);
        if (row.Note == "FAIL")
        {
            throw new InvalidOperationException($"scripted failure for row {context.RowNumber}");
        }

        Executed.Add((row, context));
        return Task.CompletedTask;
    }
}

public sealed class BulkTestContext(DbContextOptions<BulkTestContext> options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
        => configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.AddGoldpathBulk();
        modelBuilder.AddGoldpathJobs();   // adopt/takeover reads the run table
        modelBuilder.Entity<TrackedLedger>().HasMany(l => l.Entries).WithOne(e => e.Ledger).HasForeignKey(e => e.TrackedLedgerId);
        modelBuilder.Entity<TrackedInstruction>().HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.TrackedInstructionId);
        modelBuilder.Entity<TrackedInstruction>().HasMany(i => i.Tags).WithMany().UsingEntity<Dictionary<string, object>>(
            "TrackedInstructionTag",
            tag => tag.HasOne<TrackedTag>().WithMany().HasForeignKey("TagId").OnDelete(DeleteBehavior.ClientNoAction),
            instruction => instruction.HasOne<TrackedInstruction>().WithMany().HasForeignKey("InstructionId")
                .OnDelete(DeleteBehavior.ClientNoAction));
        modelBuilder.Entity<TrackedTag>();
        modelBuilder.Entity<TrackedReceipt>().HasOne<TrackedInstruction>().WithMany().HasForeignKey(r => r.InstructionId);
        modelBuilder.Entity<TrackedLine>().HasMany(l => l.Notes).WithOne().HasForeignKey(n => n.TrackedLineId);
    }
}

/// <summary>#605: an entity a test loads BEFORE the execute run, with a collection a row's handler hangs entries on.</summary>
public sealed class TrackedLedger
{
    public Guid Id { get; set; }
    public List<TrackedEntry> Entries { get; } = [];
    public List<TrackedReceipt> Receipts { get; } = [];
}

/// <summary>
/// #605: hung on a <see cref="TrackedLedger"/>, and naming a <see cref="TrackedInstruction"/> by foreign key ONLY: no
/// navigation joins the two, so only the key's value says whose it is.
/// </summary>
public sealed class TrackedReceipt
{
    public Guid Id { get; set; }
    public Guid TrackedLedgerId { get; set; }
    public Guid? InstructionId { get; set; }
}

/// <summary>#605: an entry hung on a <see cref="TrackedLedger"/>'s navigation, never added on its own.</summary>
public sealed class TrackedEntry
{
    public Guid Id { get; set; }
    public Guid TrackedLedgerId { get; set; }
    public TrackedLedger? Ledger { get; set; }
    public string Row { get; set; } = "";
}

/// <summary>#605: what a row's handler adds; its lines and tags are hung on it after the add.</summary>
public sealed class TrackedInstruction
{
    public Guid Id { get; set; }
    public string Row { get; set; } = "";
    public List<TrackedLine> Lines { get; } = [];
    public List<TrackedTag> Tags { get; } = [];
}

/// <summary>#605: an OPTIONAL child of a <see cref="TrackedInstruction"/>: EF's cascade does not drop it with its parent.</summary>
public sealed class TrackedLine
{
    public Guid Id { get; set; }
    public Guid? TrackedInstructionId { get; set; }
    public List<TrackedNote> Notes { get; } = [];
}

/// <summary>#605: an optional child of a <see cref="TrackedLine"/>: a grandchild of the row's instruction.</summary>
public sealed class TrackedNote
{
    public Guid Id { get; set; }
    public Guid? TrackedLineId { get; set; }
}

/// <summary>
/// #605: loaded before the run; a row links its instruction to it many-to-many. The join entity has no navigation and
/// no cascade (<see cref="DeleteBehavior.ClientNoAction"/>), so only the engine can leave a failed row's join out.
/// </summary>
public sealed class TrackedTag
{
    public Guid Id { get; set; }
}

public sealed class BulkFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    public BulkFixture(Action<GoldpathBulkBatchBuilder<PaymentRow>>? configure = null, int chunkSize = 500, int maxRows = 10_000, bool multiTenant = false)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new GoldpathBulkOptions { ChunkSize = chunkSize };
        Options.AddBatch<PaymentRow>("payments", b =>
        {
            b.MaxRows(maxRows).RowKey(r => r.EndToEndId).Validate((row, ctx) =>
            {
                if (row.Amount <= 0)
                {
                    ctx.Fail(nameof(row.Amount), "amount must be positive");
                }
            });
            configure?.Invoke(b);
        });

        Handler = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddDbContext<BulkTestContext>(o => o.UseSqlite(_connection));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IGoldpathBulkRowHandler<PaymentRow>>(_ => Handler);
        if (multiTenant)
        {
            services.AddSingleton<GoldpathMultiTenancyMarker>();   // what AddGoldpathMultiTenancy composes
        }

        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BulkTestContext>().Database.EnsureCreated();

        Store = new GoldpathBulkFileStore<BulkTestContext>(TimeProvider.System);
        Engine = new GoldpathBulkEngine<BulkTestContext>(
            Options, Store, TimeProvider.System, NullLogger<GoldpathBulkEngine<BulkTestContext>>.Instance);
    }

    public GoldpathBulkOptions Options { get; }

    public RecordingHandler Handler { get; }

    public ServiceProvider Services { get; }

    public GoldpathBulkFileStore<BulkTestContext> Store { get; }

    public GoldpathBulkEngine<BulkTestContext> Engine { get; }

    /// <summary>Builds a payments CSV: one line per (id, iban, amount, note) tuple.</summary>
    public static MemoryStream Csv(params (string Id, string Iban, string Amount, string? Note)[] rows)
    {
        var text = new StringBuilder("EndToEndId,Iban,Amount,Note\n");
        foreach (var (id, iban, amount, note) in rows)
        {
            text.Append(id).Append(',').Append(iban).Append(',').Append(amount).Append(',').Append(note).Append('\n');
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public IServiceScope Scope() => Services.CreateScope();

    /// <summary>Ingest + validate in one motion; returns the refreshed batch.</summary>
    public async Task<GoldpathBulkBatch> IngestValidatedAsync(MemoryStream csv, string? tenant = null)
    {
        using var scope = Scope();
        var (batch, _) = await Engine.IngestAsync(scope.ServiceProvider, "payments", csv, "payments.csv", tenant, CancellationToken.None);
        await Engine.ValidateBatchAsync(scope.ServiceProvider, batch.Id, CancellationToken.None);
        return Query(db => db.Set<GoldpathBulkBatch>().AsNoTracking().Single(b => b.Id == batch.Id));
    }

    /// <summary>Runs the full execute-job flow for whatever is Approved (plan + all chunks).</summary>
    public async Task<GoldpathJobChunk[]> ExecuteAllAsync(Guid runId)
    {
        using var scope = Scope();
        var adopted = await Engine.AdoptForExecutionAsync(scope.ServiceProvider, runId, CancellationToken.None);
        var chunks = new List<GoldpathJobChunk>();
        foreach (var batch in adopted)
        {
            for (long start = 1; start <= batch.TotalRows; start += Options.ChunkSize)
            {
                var end = Math.Min(start + Options.ChunkSize, batch.TotalRows + 1);
                var chunk = MakeChunk(chunks.Count, $"{batch.Id:N}|{start}:{end}");
                await Engine.ExecuteRangeAsync(scope.ServiceProvider, chunk, batch.Id, start, end, CancellationToken.None);
                chunks.Add(chunk);
            }
        }

        return [.. chunks];
    }

    /// <summary>Chunks are runner-constructed; tests reach the internal ctor via reflection.</summary>
    public static GoldpathJobChunk MakeChunk(int index, string payload)
        => (GoldpathJobChunk)Activator.CreateInstance(
            typeof(GoldpathJobChunk),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, [index, payload], culture: null)!;

    public T Query<T>(Func<BulkTestContext, T> query)
    {
        using var scope = Services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<BulkTestContext>());
    }

    public void Mutate(Action<BulkTestContext> mutate)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BulkTestContext>();
        mutate(db);
        db.SaveChanges();
    }

    public void Dispose()
    {
        Services.Dispose();
        _connection.Dispose();
    }
}
