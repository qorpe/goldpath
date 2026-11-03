using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Archival.Tests;

/// <summary>
/// #383's regression half. The fix truncates <c>DueAt</c> to microseconds when an entry is
/// WRITTEN. It must not change how an entry already written is VERIFIED: one written by the
/// previous engine on a store that keeps 100 ns ticks (SQL Server's <c>datetimeoffset(7)</c>,
/// Oracle's <c>TIMESTAMP(7)</c>) carries its full ticks in both the column and the hash, and
/// must keep verifying. That is the trap a tidier fix walks into: truncating inside
/// <see cref="GoldpathArchiveEnvelope.ContentHash"/> instead would re-hash such an entry at a
/// value it was never sealed with and report every one of them as tampered.
/// <para>
/// The store here is SQLite with its default <c>DateTimeOffset</c> mapping (TEXT, seven
/// fractional digits), which keeps every tick, unlike the shared fixture's binary converter
/// (0.1 ms). Verification compares no dates in SQL, so SQLite can serve it as written.
/// </para>
/// </summary>
public sealed class ArchiveDueAtPrecisionTests : IDisposable
{
    private const string Definition = "Claim";

    private sealed class TickContext(DbContextOptions<TickContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.AddGoldpathArchiveModel();
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;

    public ArchiveDueAtPrecisionTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<TickContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TickContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task An_entry_the_previous_engine_sealed_at_full_ticks_still_verifies()
    {
        // Exactly what the previous engine wrote: DueAt at full 100 ns ticks, in the column
        // AND in the content hash. The hash is the LITERAL the previous engine sealed (see
        // Expected), never recomputed here: a test that sealed with today's ContentHash would
        // agree with any change made to it. Same instant as Expected's, written at +03:00.
        var dueAt = new DateTimeOffset(2026, 1, 15, 13, 30, 0, TimeSpan.FromHours(3)).AddTicks(1_234_567);
        Assert.NotEqual(0, dueAt.UtcTicks % TimeSpan.TicksPerMicrosecond);
        const string document = """{"Reference":"CLM-1"}""";
        const string contentHash = Expected;
        var chainHash = GoldpathArchiveEnvelope.ChainHash(contentHash, "", 1);

        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TickContext>();
            db.Add(new GoldpathArchiveEntry
            {
                Definition = Definition,
                AggregateKey = "claim-1",
                Tenant = "agency-7",
                Document = document,
                SchemaVersion = GoldpathArchiveEnvelope.SchemaVersion,
                DueAt = dueAt,
                ArchivedAt = dueAt.AddDays(31),
                ChainIndex = 1,
                ContentHash = contentHash,
                ChainHash = chainHash,
                PreviousHash = "",
            });
            db.Add(new GoldpathArchiveChainState { Definition = Definition, LastIndex = 1, LastHash = chainHash });
            await db.SaveChangesAsync();
        }

        using (var scope = _services.CreateScope())
        {
            // The store kept every tick — otherwise this test would prove nothing about them.
            var stored = await scope.ServiceProvider.GetRequiredService<TickContext>()
                .Set<GoldpathArchiveEntry>().AsNoTracking().SingleAsync();
            Assert.Equal(dueAt.UtcTicks, stored.DueAt.UtcTicks);

            var engine = new GoldpathArchiveEngine<TickContext>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<TickContext>>.Instance);
            var findings = await engine.VerifySliceAsync(scope.ServiceProvider, ArchiveFixture.ClaimArchive(), 1, 1, CancellationToken.None);
            Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => f.Problem)));
        }
    }

    [Fact]
    public async Task A_new_entry_is_hashed_and_stored_at_the_same_microsecond()
    {
        // Seeded and archived in ONE scope, so the candidate's DueAt is the tracked value with
        // its 100 ns ticks (the shared fixture's store would round it away on a re-read). The
        // entry is read back from the change tracker: exactly what the engine handed the store.
        using var fixture = new ArchiveFixture();
        var closedAt = new DateTimeOffset(2026, 1, 15, 13, 30, 0, TimeSpan.FromHours(3)).AddTicks(1_234_567);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveTestContext>();
        db.Claims.Add(new Claim { Id = Guid.NewGuid(), Reference = "CLM-1", ClosedAt = closedAt });
        await db.SaveChangesAsync();

        Assert.Equal(1, await fixture.Engine.ArchiveNextBatchAsync(scope.ServiceProvider, ArchiveFixture.ClaimArchive(), 10, CancellationToken.None));

        var entry = Assert.Single(db.Set<GoldpathArchiveEntry>().Local);
        // Truncated, never rounded: .1234567 s keeps .123456 s, at the offset it was written in.
        Assert.Equal(closedAt.UtcTicks - 7, entry.DueAt.UtcTicks);
        Assert.Equal(closedAt.Offset, entry.DueAt.Offset);
        Assert.Equal(
            GoldpathArchiveEnvelope.ContentHash(entry.SchemaVersion, entry.Definition, entry.AggregateKey, entry.Tenant, entry.DueAt, entry.Document),
            entry.ContentHash);
    }

    [Fact]
    public async Task A_DueAt_already_on_a_whole_microsecond_is_written_unchanged()
    {
        using var fixture = new ArchiveFixture();
        var closedAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero).AddTicks(1_234_560);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveTestContext>();
        db.Claims.Add(new Claim { Id = Guid.NewGuid(), Reference = "CLM-2", ClosedAt = closedAt });
        await db.SaveChangesAsync();

        await fixture.Engine.ArchiveNextBatchAsync(scope.ServiceProvider, ArchiveFixture.ClaimArchive(), 10, CancellationToken.None);

        Assert.Equal(closedAt.UtcTicks, Assert.Single(db.Set<GoldpathArchiveEntry>().Local).DueAt.UtcTicks);
    }

    [Fact]
    public void The_content_hash_format_is_unchanged()
    {
        // Pinned against a value computed by the previous engine: the canonical string, its
        // tick encoding and the hash function did not move, so no entry needs a re-stamp.
        var dueAt = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var hash = GoldpathArchiveEnvelope.ContentHash(1, "Claim", "claim-1", "agency-7", dueAt, """{"Reference":"CLM-1"}""");

        Assert.Equal(Expected, hash);
    }

    // sha256("1|Claim|claim-1|agency-7|639040698001234567|{"Reference":"CLM-1"}"), computed outside .NET.
    private const string Expected = "d32a32ec1e127866c634b1954edb30fd03a79ebb97f1dcf861375bda11d013c2";
}
