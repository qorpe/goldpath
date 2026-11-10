using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The archival lifecycle on real PostgreSQL: archive-moves-graph, retrieval, a raw-SQL
/// tamper caught by verification, legal hold stopping the purge, and row retention — the
/// exact story the RFC promises, against the real provider. The same facts run on Oracle
/// (<see cref="ArchivalOracleTests"/>, G7 ORA-6 #409).
/// </summary>
public class ArchivalTests : IAsyncLifetime
{
    private readonly SuiteStore _store;

    /// <summary>The suite on PostgreSQL, in the integration lane.</summary>
    public ArchivalTests()
        : this(SuiteStore.PostgreSql())
    {
    }

    /// <summary>The suite on the given store (the Oracle twin, ORA-6 #409).</summary>
    protected ArchivalTests(SuiteStore store) => _store = store;

    public sealed class Policy
    {
        public Guid Id { get; set; }
        public string Holder { get; set; } = "";
        public DateTimeOffset? CancelledAt { get; set; }
        public List<Endorsement> Endorsements { get; set; } = [];
    }

    public sealed class Endorsement
    {
        public Guid Id { get; set; }
        public Guid PolicyId { get; set; }
        public string Change { get; set; } = "";
    }

    public sealed class CallRecord
    {
        public long Id { get; set; }
        public DateTimeOffset RecordedAt { get; set; }
        public bool RolledUp { get; set; }
    }

    public sealed class ArchDb(DbContextOptions<ArchDb> options) : DbContext(options)
    {
        public DbSet<Policy> Policies => Set<Policy>();
        public DbSet<CallRecord> Calls => Set<CallRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Policy>().Property(p => p.Id).ValueGeneratedNever();
            modelBuilder.Entity<Endorsement>().Property(e => e.Id).ValueGeneratedNever();
            modelBuilder.Entity<Policy>().HasMany(p => p.Endorsements).WithOne()
                .HasForeignKey(e => e.PolicyId).OnDelete(DeleteBehavior.Cascade);
            modelBuilder.AddGoldpathArchiveModel();
        }
    }

    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _store.StartAsync<ArchDb>(options => new(options));
        var services = new ServiceCollection();
        services.AddDbContext<ArchDb>(_store.Use);
        // The REAL DataProtection module: classify once, the archive redacts on erasure.
        var dataProtection = new GoldpathDataProtectionOptions();
        dataProtection.Catalog(c => c.Classify<Policy>(p => p.Holder, GoldpathDataClass.Personal));
        services.AddRedaction();
        services.AddSingleton<IGoldpathDataProtector>(sp =>
            new GoldpathDataProtector(dataProtection, sp.GetRequiredService<IRedactorProvider>()));
        _services = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _store.StopAsync();
    }

    private static GoldpathArchivalOptions PolicyArchival(int retainYears = 10, bool deleteHotRows = true)
    {
        var options = new GoldpathArchivalOptions();
        options.AddArchive<Policy>(a =>
        {
            a.Graph(p => p.Endorsements)
                .Key(p => p.Id)
                .DueWhen(p => p.CancelledAt != null, p => p.CancelledAt!.Value)
                .ArchiveAfter(TimeSpan.FromDays(30))
                .RetainFor(retainYears);
            if (deleteHotRows)
            {
                a.DeleteHotRowsAfterArchive();
            }
        });
        options.AddRowRetention<CallRecord>(r => r
            .After(TimeSpan.FromDays(90), c => c.RecordedAt)
            .Where(c => c.RolledUp));
        return options;
    }

    /// <summary>
    /// #547: a chain state saved before its first entry. Every candidate of a definition's first batch raced away (its
    /// row was gone by the time it was loaded), so the batch archives nothing, yet the chain state is still saved, and
    /// it holds the genesis anchor in BOTH hashes. On Oracle, which stores <c>''</c> as NULL, that save is refused
    /// unless <c>LastHash</c> is nullable there as <c>PurgedHeadHash</c> is. The next batch then links its first entry
    /// to that anchor, and the chain verifies.
    /// </summary>
    [Fact]
    public async Task A_first_batch_whose_every_candidate_raced_away_keeps_the_genesis_anchor()
    {
        var engine = new GoldpathArchiveEngine<ArchDb>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<ArchDb>>.Instance);
        var definition = PolicyArchival().Archives[0];
        var load = typeof(GoldpathArchiveDefinition).GetProperty("LoadAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var realLoad = load.GetValue(definition)!;
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            db.Policies.Add(new Policy { Id = Guid.NewGuid(), Holder = "holder", CancelledAt = DateTimeOffset.UtcNow.AddDays(-60) });
            await db.SaveChangesAsync();
        }

        load.SetValue(definition, (Func<DbContext, string, CancellationToken, Task<GoldpathArchiveCandidate?>>)((_, _, _) => Task.FromResult<GoldpathArchiveCandidate?>(null)));
        using (var scope = _services.CreateScope())
        {
            Assert.Equal(0, await engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, 10, CancellationToken.None));
        }

        using (var scope = _services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<ArchDb>().Set<GoldpathArchiveChainState>().AsNoTracking().SingleAsync();
            Assert.Equal((0L, "", ""), (state.LastIndex, state.LastHash, state.PurgedHeadHash));
        }

        load.SetValue(definition, realLoad);
        using (var scope = _services.CreateScope())
        {
            Assert.Equal(1, await engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, 10, CancellationToken.None));
        }

        using (var scope = _services.CreateScope())
        {
            var entry = await scope.ServiceProvider.GetRequiredService<ArchDb>().Set<GoldpathArchiveEntry>().AsNoTracking().SingleAsync();
            Assert.Equal((1L, ""), (entry.ChainIndex, entry.PreviousHash));
            Assert.Empty(await engine.VerifySliceAsync(scope.ServiceProvider, definition, 1, 1, CancellationToken.None));
        }
    }

    /// <summary>
    /// #383: PostgreSQL's <c>timestamptz</c> keeps microseconds, a <see cref="DateTimeOffset"/>
    /// carries 100 ns ticks. An entry whose <c>DueAt</c> was hashed at full ticks and stored at
    /// microseconds can never verify again — "document does not match its content hash" with
    /// nothing tampered. The candidate here is archived in the SAME scope that saved it, so its
    /// tracked <c>CancelledAt</c> still carries the sub-microsecond ticks a column read would
    /// have dropped: the shape a <c>DueWhen</c> date selector that reads the clock produces.
    /// </summary>
    [Fact]
    public async Task An_entry_whose_DueAt_carries_sub_microsecond_ticks_verifies()
    {
        var engine = new GoldpathArchiveEngine<ArchDb>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<ArchDb>>.Instance);
        var options = PolicyArchival();
        var definition = options.Archives[0];
        // 0.1234567 s past the minute: the trailing 7 is the tick PostgreSQL cannot hold.
        var cancelledAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);
        Assert.NotEqual(0, cancelledAt.UtcTicks % 10);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            db.Policies.Add(new Policy { Id = Guid.NewGuid(), Holder = "holder", CancelledAt = cancelledAt });
            await db.SaveChangesAsync();
            var archived = await engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, 10, CancellationToken.None);
            Assert.Equal(1, archived);
        }

        using (var scope = _services.CreateScope())
        {
            var findings = await engine.VerifySliceAsync(scope.ServiceProvider, definition, 1, 1, CancellationToken.None);
            Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => f.Problem)));

            // What was stored is what was hashed: the microsecond every store can hold (Oracle's TIMESTAMP(7) keeps
            // the 100 ns tick, PostgreSQL's timestamptz does not; the engine truncates for both).
            var stored = await scope.ServiceProvider.GetRequiredService<ArchDb>().Set<GoldpathArchiveEntry>().AsNoTracking().SingleAsync();
            Assert.Equal(cancelledAt.AddTicks(-7), stored.DueAt);

            // And the admin surface's own verification agrees.
            var admin = new GoldpathArchiveAdminService<ArchDb>(
                engine, options, _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            Assert.Empty(await admin.VerifyAsync("Policy", CancellationToken.None));
        }
    }

    [Fact]
    public async Task The_full_lifecycle_holds()
    {
        var engine = new GoldpathArchiveEngine<ArchDb>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<ArchDb>>.Instance);
        var options = PolicyArchival(retainYears: 0);
        var definition = options.Archives[0];

        // Seed: three cancelled policies with endorsements + purgeable/guarded call records.
        var keys = new List<string>();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            for (var i = 0; i < 3; i++)
            {
                var policy = new Policy
                {
                    Id = Guid.NewGuid(),
                    Holder = $"holder-{i}",
                    CancelledAt = DateTimeOffset.UtcNow.AddDays(-90),
                    Endorsements = [new Endorsement { Id = Guid.NewGuid(), Change = $"change-{i}" }],
                };
                db.Policies.Add(policy);
                keys.Add(policy.Id.ToString());
            }

            for (var i = 0; i < 50; i++)
            {
                db.Calls.Add(new CallRecord { RecordedAt = DateTimeOffset.UtcNow.AddDays(-200), RolledUp = i % 2 == 0 });
            }

            await db.SaveChangesAsync();
        }

        // ARCHIVE: graphs move, chain seals.
        using (var scope = _services.CreateScope())
        {
            var archived = await engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, 10, CancellationToken.None);
            Assert.Equal(3, archived);
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            Assert.Equal(0, await db.Policies.CountAsync());
            Assert.Equal(3, await db.Set<GoldpathArchiveEntry>().CountAsync());
        }

        // RETRIEVE: the finance budget's code path (indexed lookup).
        using (var scope = _services.CreateScope())
        {
            var entry = await engine.RetrieveAsync(scope.ServiceProvider, "Policy", keys[1], CancellationToken.None);
            Assert.NotNull(entry);
            Assert.Contains("change-1", entry.Document, StringComparison.Ordinal);
        }

        // TAMPER via raw SQL (the hostile-DBA scenario) → verification catches it.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            // Name-independent tamper (archive order is Guid order — never bet on which
            // holder landed at chain-1): append a byte, guaranteed content change.
            var affected = await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GoldpathArchiveEntries" SET "Document" = "Document" || ' ' WHERE "ChainIndex" = 1""");
            Assert.Equal(1, affected);
            var findings = await engine.VerifySliceAsync(scope.ServiceProvider, definition, 1, 3, CancellationToken.None);
            var finding = Assert.Single(findings, f => f.ChainIndex == 1);
            Assert.Contains("content hash", finding.Problem, StringComparison.Ordinal);

            // repair the tamper so the purge phase below starts from a clean chain
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE "GoldpathArchiveEntries" SET "Document" = rtrim("Document") WHERE "ChainIndex" = 1""");
            Assert.Empty(await engine.VerifySliceAsync(scope.ServiceProvider, definition, 1, 3, CancellationToken.None));
        }

        // LEGAL HOLD stops the purge at the held entry; lifting releases it.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            // Archive order is KEY order (Guid), not insertion order — hold the chain's middle.
            var heldKey = await db.Set<GoldpathArchiveEntry>().Where(e => e.ChainIndex == 2).Select(e => e.AggregateKey).SingleAsync();
            db.Add(new GoldpathLegalHold { Definition = "Policy", AggregateKey = heldKey, CaseReference = "LIT-9", PlacedBy = "counsel", PlacedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            Assert.Equal(1, await engine.PurgeExpiredEntriesAsync(scope.ServiceProvider, definition, 10, CancellationToken.None));
            Assert.Equal(2, await db.Set<GoldpathArchiveEntry>().CountAsync());
        }

        // ERASURE (KVKK): the classified field redacts, the chain STILL verifies, the
        // evidence row answers the authority.
        using (var scope = _services.CreateScope())
        {
            var admin = new GoldpathArchiveAdminService<ArchDb>(
                engine, options, _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            // The middle entry still carries the (lifted-later) hold story; erase the tail.
            var erasableKey = await db.Set<GoldpathArchiveEntry>()
                .OrderByDescending(e => e.ChainIndex).Select(e => e.AggregateKey).FirstAsync();

            var erased = await admin.EraseAsync("Policy", erasableKey, "citizen-42", "dpo", "KVKK-1", CancellationToken.None);
            Assert.True(erased.Ok, erased.Message);

            var entry = await db.Set<GoldpathArchiveEntry>().AsNoTracking().SingleAsync(e => e.AggregateKey == erasableKey);
            Assert.NotNull(entry.ErasedAt);
            Assert.DoesNotContain("holder-", entry.Document, StringComparison.Ordinal);
            Assert.Empty(await admin.VerifyAsync("Policy", CancellationToken.None));   // lawful divergence
            Assert.Single(await admin.GetErasuresAsync(10, CancellationToken.None));
        }

        // ROW RETENTION: only guarded, aged rows leave.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            var retention = options.RowRetentions[0];
            Assert.Equal(25, await retention.CountDueAsync(db, DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.Equal(25, await retention.PurgeBatchAsync(db, DateTimeOffset.UtcNow, 100, CancellationToken.None));
            Assert.Equal(25, await db.Calls.CountAsync());   // the unguarded half survives
        }
    }

    /// <summary>
    /// RFC goldpath-oracle §2.5: discovery pages <c>BatchSize * 2</c> keys and filters out the archived ones with one
    /// <c>Contains</c>, so a <c>BatchSize</c> of 600 sends 1,200 keys, past Oracle's 1,000-element <c>IN</c> list. A copy
    /// (no hot-row delete) is the shape that needs the filter: the archived prefix stays due, so every later page holds
    /// keys already archived, and an unfiltered page would archive them twice.
    /// </summary>
    [Fact]
    public async Task A_batch_of_600_discovers_1200_keys_and_archives_every_due_aggregate_exactly_once()
    {
        const int Policies = 1_300;
        const int BatchSize = 600;
        var engine = new GoldpathArchiveEngine<ArchDb>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<ArchDb>>.Instance);
        var definition = PolicyArchival(deleteHotRows: false).Archives[0];
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            for (var i = 0; i < Policies; i++)
            {
                db.Policies.Add(new Policy { Id = Guid.NewGuid(), Holder = $"holder-{i}", CancelledAt = DateTimeOffset.UtcNow.AddDays(-90) });
            }

            await db.SaveChangesAsync();
        }

        var moved = new List<int>();
        for (var pass = 0; pass < 5; pass++)
        {
            using var scope = _services.CreateScope();
            moved.Add(await engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, BatchSize, CancellationToken.None));
        }

        // 600, then 600 (its pages each carry 1,200 keys, 600 of them archived), then the last 100, then nothing.
        Assert.Equal([600, 600, 100, 0, 0], moved);
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ArchDb>();
            var keys = await db.Set<GoldpathArchiveEntry>().AsNoTracking().Select(e => e.AggregateKey).ToListAsync();
            Assert.Equal(Policies, keys.Count);
            Assert.Equal(Policies, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(Policies, await db.Policies.CountAsync());   // a copy: the hot rows stay
            Assert.Empty(await engine.VerifySliceAsync(scope.ServiceProvider, definition, 1, Policies, CancellationToken.None));
        }
    }
}

/// <summary>
/// The archival suite on Oracle (G7 ORA-6, #409; RFC goldpath-oracle §7, Archival). The chain's genesis anchor is the
/// empty hash, and Oracle stores <c>''</c> as NULL: since #547 the anchor columns are nullable there, "" is written as
/// NULL and read back as "", so a definition's first archive is stored and its chain verifies as on every provider.
/// </summary>
[Trait("Category", "Oracle")]
[Collection(OracleCollection.Name)]
public sealed class ArchivalOracleTests(OracleFixture oracle) : ArchivalTests(SuiteStore.Oracle(oracle, "GP_ARCH"));
