using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Compliance.Redaction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// S-FIN-02 (scenario campaign): "A litigation hold survives an erasure request, and the chain
/// still verifies." An archived payment instruction on real PostgreSQL is placed under legal
/// hold by counsel; a KVKK erasure arrives and is REFUSED in words that name the hold, with
/// nothing touched; counsel lifts the hold; the erasure runs; the personal data is gone from
/// the stored document, the chain verifies (a lawful divergence, not tamper), and the erasure
/// receipt names who asked and when. Replay: dotnet test --filter FullyQualifiedName~ScenarioFin02.
/// </summary>
public sealed class ScenarioFin02Tests : IAsyncLifetime
{
    public sealed class Instruction
    {
        public Guid Id { get; set; }
        public string Beneficiary { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTimeOffset? SettledAt { get; set; }
    }

    public sealed class InstructionDb(DbContextOptions<InstructionDb> options) : DbContext(options)
    {
        public DbSet<Instruction> Instructions => Set<Instruction>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Instruction>().Property(i => i.Id).ValueGeneratedNever();
            modelBuilder.AddGoldpathArchiveModel();
        }
    }

    private const string Beneficiary = "Ayse Example";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var services = new ServiceCollection();
        services.AddDbContext<InstructionDb>(o => o.UseNpgsql(_postgres.GetConnectionString()));
        // The REAL DataProtection module: the catalog is what tells erasure WHAT to redact.
        var dataProtection = new GoldpathDataProtectionOptions();
        dataProtection.Catalog(c => c.Classify<Instruction>(i => i.Beneficiary, GoldpathDataClass.Personal));
        services.AddRedaction();
        services.AddSingleton<IGoldpathDataProtector>(sp =>
            new GoldpathDataProtector(dataProtection, sp.GetRequiredService<IRedactorProvider>()));
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InstructionDb>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task A_held_instruction_refuses_erasure_until_the_hold_is_lifted_and_the_chain_still_verifies()
    {
        var ct = CancellationToken.None;
        var engine = new GoldpathArchiveEngine<InstructionDb>(TimeProvider.System, NullLogger<GoldpathArchiveEngine<InstructionDb>>.Instance);
        var options = new GoldpathArchivalOptions();
        options.AddArchive<Instruction>(a => a
            .Key(i => i.Id)
            .DueWhen(i => i.SettledAt != null, i => i.SettledAt!.Value)
            .ArchiveAfter(TimeSpan.FromDays(30))
            .RetainFor(10)
            .DeleteHotRowsAfterArchive());
        var admin = new GoldpathArchiveAdminService<InstructionDb>(
            engine, options, _services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);

        // GIVEN an archived instruction (two of them, so the chain has a neighbour to break).
        var held = Guid.NewGuid();
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InstructionDb>();
            db.Instructions.Add(new Instruction { Id = held, Beneficiary = Beneficiary, Amount = 1250m, SettledAt = DateTimeOffset.UtcNow.AddDays(-90) });
            db.Instructions.Add(new Instruction { Id = Guid.NewGuid(), Beneficiary = "Other Example", Amount = 10m, SettledAt = DateTimeOffset.UtcNow.AddDays(-90) });
            await db.SaveChangesAsync(ct);
        }

        // The archival job runs in its own scope, reading what the store holds. Archiving from
        // the context that just saved would hash 100 ns ticks the column cannot keep.
        using (var scope = _services.CreateScope())
        {
            Assert.Equal(2, await engine.ArchiveNextBatchAsync(scope.ServiceProvider, options.Archives[0], 10, ct));
        }

        var key = held.ToString();
        Assert.Empty(await admin.VerifyAsync("Instruction", ct));

        // … under legal hold.
        var hold = await admin.PlaceHoldAsync("Instruction", key, "LIT-2026-114", "counsel", ct);
        Assert.True(hold.Ok, hold.Message);

        // WHEN a KVKK erasure arrives for it → refused, explicitly, and nothing is touched.
        var refused = await admin.EraseAsync("Instruction", key, "subject-77", "dpo", "KVKK-2026-9", ct);
        Assert.False(refused.Ok);
        Assert.Contains("legal hold", refused.Message, StringComparison.Ordinal);
        var untouched = await EntryAsync(key);
        Assert.Null(untouched.ErasedAt);
        Assert.Contains(Beneficiary, untouched.Document, StringComparison.Ordinal);
        Assert.Empty(await admin.GetErasuresAsync(10, ct));

        // Counsel lifts the hold — the same row records who and when.
        var lifted = await admin.LiftHoldAsync("Instruction", key, "counsel", ct);
        Assert.True(lifted.Ok, lifted.Message);
        var liftedHold = Assert.Single(await admin.GetHoldsAsync(includeLifted: true, 10, ct));
        Assert.Equal("counsel", liftedHold.LiftedBy);
        Assert.NotNull(liftedHold.LiftedAt);

        // … and the erasure runs.
        var before = DateTimeOffset.UtcNow;
        var erased = await admin.EraseAsync("Instruction", key, "subject-77", "dpo", "KVKK-2026-9", ct);
        Assert.True(erased.Ok, erased.Message);

        // THEN the personal data is gone from the stored document,
        var entry = await EntryAsync(key);
        Assert.NotNull(entry.ErasedAt);
        Assert.DoesNotContain(Beneficiary, entry.Document, StringComparison.Ordinal);

        // the chain still verifies (the erasure mark makes the divergence lawful),
        Assert.Empty(await admin.VerifyAsync("Instruction", ct));

        // and the receipt names actor and time.
        var receipt = Assert.Single(await admin.GetErasuresAsync(10, ct));
        Assert.Equal("dpo", receipt.RequestedBy);
        Assert.Equal("subject-77", receipt.SubjectKey);
        Assert.InRange(receipt.RequestedAt, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    private async Task<GoldpathArchiveEntry> EntryAsync(string key)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InstructionDb>()
            .Set<GoldpathArchiveEntry>().AsNoTracking().SingleAsync(e => e.AggregateKey == key);
    }
}
