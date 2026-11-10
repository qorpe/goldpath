using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Archival.Tests;

/// <summary>
/// Mapping and plan-math contracts: retrieval rides the unique (Definition, AggregateKey)
/// index, the chain rides (Definition, ChainIndex), and the jobs' chunk plans are exact —
/// silently-dropped indexes or off-by-one chunk math die here.
/// </summary>
/// <remarks>
/// The model is built FRESH for every test, straight from <c>AddGoldpathArchiveModel</c> under
/// the provider's conventions, never read from a <see cref="DbContext"/>. EF builds a context
/// type's model ONCE per process and caches it, so in Stryker's long-lived test host the mapping
/// ran for the first test only and never under a mutant: 18 mapping statements in
/// <c>ArchiveModel.cs</c> survived these very assertions (open thread T33, mutation-heavy run
/// 39410990059). Bulk's suite had the same blind spot and the same cure.
/// </remarks>
public class ModelAndPlanContractTests
{
    private Microsoft.EntityFrameworkCore.Metadata.IModel Model { get; } = BuildModel();

    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildModel(bool asOracle = false)
    {
        var builder = new ModelBuilder(Microsoft.EntityFrameworkCore.Metadata.Conventions.SqliteConventionSetBuilder.Build());
        if (asOracle)
        {
            // What the Oracle EF provider leaves on the model before OnModelCreating, and the only thing Archival reads.
            builder.HasAnnotation("Oracle:ValueGenerationStrategy", "IdentityColumn");
        }

        builder.AddGoldpathArchiveModel();
        return builder.FinalizeModel();
    }

    /// <summary>The three columns that hold the empty genesis hash (#547).</summary>
    private static IEnumerable<Microsoft.EntityFrameworkCore.Metadata.IProperty> GenesisAnchors(Microsoft.EntityFrameworkCore.Metadata.IModel model) =>
    [
        model.FindEntityType(typeof(GoldpathArchiveEntry))!.FindProperty(nameof(GoldpathArchiveEntry.PreviousHash))!,
        model.FindEntityType(typeof(GoldpathArchiveChainState))!.FindProperty(nameof(GoldpathArchiveChainState.LastHash))!,
        model.FindEntityType(typeof(GoldpathArchiveChainState))!.FindProperty(nameof(GoldpathArchiveChainState.PurgedHeadHash))!,
    ];

    /// <summary>#547: everywhere but Oracle the genesis anchors stay required and unconverted — every chain byte-identical.</summary>
    [Fact]
    public void Outside_Oracle_the_genesis_anchors_stay_required_and_unconverted()
    {
        Assert.All(GenesisAnchors(Model), p =>
        {
            Assert.False(p.IsNullable, p.Name);
            Assert.Null(p.GetValueConverter());
            Assert.Equal(64, p.GetMaxLength());
        });
    }

    /// <summary>
    /// #547 (owner decision A): on Oracle, which stores <c>''</c> as NULL, the genesis anchors are nullable, the empty
    /// hash is written as NULL and any other hash as itself, and a NULL read back is the empty hash on the entity.
    /// </summary>
    [Fact]
    public void On_Oracle_the_empty_genesis_hash_is_written_as_null_and_read_back_as_empty()
    {
        const string hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        Assert.All(GenesisAnchors(BuildModel(asOracle: true)), p =>
        {
            Assert.True(p.IsNullable, p.Name);
            Assert.Equal(64, p.GetMaxLength());
            var converter = p.GetValueConverter()!;
            Assert.Null(converter.ConvertToProvider(""));
            Assert.Equal(hash, converter.ConvertToProvider(hash));
            Assert.Equal(hash, converter.ConvertFromProvider(hash));
        });

        // EF writes a NULL it reads into the mapped backing field, past the converter; the property reads it as "".
        var entry = new GoldpathArchiveEntry();
        var state = new GoldpathArchiveChainState();
        foreach (var (target, field) in new (object, string)[] { (entry, "_previousHash"), (state, "_lastHash"), (state, "_purgedHeadHash") })
        {
            target.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(target, null);
        }

        Assert.Equal(("", "", ""), (entry.PreviousHash, state.LastHash, state.PurgedHeadHash));
        entry.PreviousHash = hash;
        state.LastHash = hash;
        state.PurgedHeadHash = hash;
        Assert.Equal((hash, hash, hash), (entry.PreviousHash, state.LastHash, state.PurgedHeadHash));
    }

    [Fact]
    public void Entries_carry_the_retrieval_and_chain_indexes_with_bounded_columns()
    {
        var entry = Model.FindEntityType(typeof(GoldpathArchiveEntry))!;
        Assert.Equal("GoldpathArchiveEntries", entry.GetTableName());
        Assert.Contains(entry.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(GoldpathArchiveEntry.Definition), nameof(GoldpathArchiveEntry.AggregateKey)]));
        Assert.Contains(entry.GetIndexes(), i => i.IsUnique && i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(GoldpathArchiveEntry.Definition), nameof(GoldpathArchiveEntry.ChainIndex)]));
        Assert.Contains(entry.GetIndexes(), i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(GoldpathArchiveEntry.Definition), nameof(GoldpathArchiveEntry.ArchivedAt)]));
        Assert.Equal(120, entry.FindProperty(nameof(GoldpathArchiveEntry.Definition))!.GetMaxLength());
        Assert.Equal(256, entry.FindProperty(nameof(GoldpathArchiveEntry.AggregateKey))!.GetMaxLength());
        Assert.Equal(64, entry.FindProperty(nameof(GoldpathArchiveEntry.Tenant))!.GetMaxLength());
        Assert.Equal(64, entry.FindProperty(nameof(GoldpathArchiveEntry.ContentHash))!.GetMaxLength());
        Assert.Equal(64, entry.FindProperty(nameof(GoldpathArchiveEntry.ChainHash))!.GetMaxLength());
        Assert.Equal(64, entry.FindProperty(nameof(GoldpathArchiveEntry.PreviousHash))!.GetMaxLength());
    }

    [Fact]
    public void Chain_state_holds_and_erasure_tables_are_mapped()
    {
        var state = Model.FindEntityType(typeof(GoldpathArchiveChainState))!;
        Assert.Equal("GoldpathArchiveChainState", state.GetTableName());
        Assert.Equal([nameof(GoldpathArchiveChainState.Definition)], state.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal(120, state.FindProperty(nameof(GoldpathArchiveChainState.Definition))!.GetMaxLength());
        Assert.Equal(64, state.FindProperty(nameof(GoldpathArchiveChainState.LastHash))!.GetMaxLength());
        Assert.Equal(64, state.FindProperty(nameof(GoldpathArchiveChainState.PurgedHeadHash))!.GetMaxLength());

        var hold = Model.FindEntityType(typeof(GoldpathLegalHold))!;
        Assert.Equal("GoldpathLegalHolds", hold.GetTableName());
        Assert.Contains(hold.GetIndexes(), i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(GoldpathLegalHold.Definition), nameof(GoldpathLegalHold.AggregateKey), nameof(GoldpathLegalHold.LiftedAt)]));
        Assert.Equal(256, hold.FindProperty(nameof(GoldpathLegalHold.CaseReference))!.GetMaxLength());
        Assert.Equal(256, hold.FindProperty(nameof(GoldpathLegalHold.PlacedBy))!.GetMaxLength());
        Assert.Equal(120, hold.FindProperty(nameof(GoldpathLegalHold.Definition))!.GetMaxLength());
        Assert.Equal(256, hold.FindProperty(nameof(GoldpathLegalHold.AggregateKey))!.GetMaxLength());
        Assert.Equal(256, hold.FindProperty(nameof(GoldpathLegalHold.LiftedBy))!.GetMaxLength());

        var erasure = Model.FindEntityType(typeof(GoldpathErasureRecord))!;
        Assert.Equal("GoldpathErasureRecords", erasure.GetTableName());
        Assert.Equal(256, erasure.FindProperty(nameof(GoldpathErasureRecord.SubjectKey))!.GetMaxLength());
        Assert.Equal(256, erasure.FindProperty(nameof(GoldpathErasureRecord.RequestedBy))!.GetMaxLength());

        // The hash the erasure keeps of the content it destroyed: a SHA-256 in hex, 64 characters.
        Assert.Equal(64, Model.FindEntityType(typeof(GoldpathArchiveEntry))!
            .FindProperty(nameof(GoldpathArchiveEntry.PreErasureContentHash))!.GetMaxLength());
        Assert.Contains(erasure.GetIndexes(), i => i.Properties.Select(p => p.Name)
            .SequenceEqual([nameof(GoldpathErasureRecord.SubjectKey)]));
    }

    [Fact]
    public void Entity_defaults_are_the_states_the_engine_relies_on()
    {
        var entry = new GoldpathArchiveEntry();
        Assert.Equal("", entry.Definition);
        Assert.Equal("", entry.AggregateKey);
        Assert.Equal("", entry.Document);
        Assert.Equal("", entry.ContentHash);
        Assert.Equal("", entry.ChainHash);
        Assert.Equal("", entry.PreviousHash);
        Assert.Null(entry.Tenant);
        Assert.Null(entry.ErasedAt);

        var state = new GoldpathArchiveChainState();
        Assert.Equal("", state.Definition);
        Assert.Equal(0, state.LastIndex);
        Assert.Equal("", state.LastHash);           // the genesis anchor IS the empty hash
        Assert.Equal(0, state.PurgedThroughIndex);
        Assert.Equal("", state.PurgedHeadHash);

        var hold = new GoldpathLegalHold();
        Assert.Equal("", hold.CaseReference);
        Assert.Equal("", hold.Definition);
        Assert.Equal("", hold.AggregateKey);
        Assert.Equal("", hold.PlacedBy);
        Assert.Null(hold.LiftedAt);
        Assert.Null(hold.LiftedBy);

        var erasure = new GoldpathErasureRecord();
        Assert.Equal("", erasure.SubjectKey);
        Assert.Equal("", erasure.RequestedBy);
        Assert.Equal(0, erasure.EntriesAffected);
        Assert.Null(erasure.Detail);
    }

    [Fact]
    public void Builder_metadata_is_exact()
    {
        var options = new GoldpathArchivalOptions();
        options.AddArchive<Claim>(a => a
            .Named("claim-file")
            .Graph(c => c.Decisions, c => c.Documents)
            .GraphPath("Decisions.Attachments")
            .Key(c => c.Id)
            .DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value)
            .ArchiveAfter(TimeSpan.FromDays(365))
            .RetainFor(years: 10)
            .DeleteHotRowsAfterArchive());

        var definition = options.Archives[0];
        Assert.Equal("claim-file", definition.Name);
        Assert.Equal(typeof(Claim), definition.RootType);
        Assert.Equal(["Decisions", "Documents", "Decisions.Attachments"], definition.GraphPaths);
        Assert.Equal(TimeSpan.FromDays(365), definition.ArchiveAfter);
        Assert.Equal(TimeSpan.FromDays(3652.5), definition.RetainFor);
        Assert.True(definition.DeleteHotRows);

        var plain = new GoldpathArchivalOptions();
        plain.AddArchive<Claim>(a => a.Key(c => c.Id).DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value));
        Assert.Equal("Claim", plain.Archives[0].Name);       // default name = root type
        Assert.False(plain.Archives[0].DeleteHotRows);
        Assert.Null(plain.Archives[0].RetainFor);
        Assert.Equal(TimeSpan.Zero, plain.Archives[0].ArchiveAfter);
        Assert.Equal(200, plain.BatchSize);
    }

    [Fact]
    public async Task Archive_job_plan_chunk_math_is_exact()
    {
        using var fixture = new ArchiveFixture();
        for (var i = 0; i < 5; i++)
        {
            fixture.SeedClaim();
        }

        var archival = new GoldpathArchivalOptions { BatchSize = 2 };
        archival.AddArchive<Claim>(a => a
            .Key(c => c.Id)
            .DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value)
            .DeleteHotRowsAfterArchive());

        var job = new GoldpathArchiveJob<ArchiveTestContext>(fixture.Engine, archival);
        using var scope = fixture.Scope();
        var context = CreateContext(scope.ServiceProvider);
        var plan = await job.PlanAsync(context, CancellationToken.None);

        Assert.Equal(["Claim|0", "Claim|1", "Claim|2"], plan.ChunkPayloads);   // ceil(5/2)
        Assert.Equal(5, plan.TotalItems);
    }

    [Fact]
    public async Task Purge_job_plan_prefixes_rows_and_entries_targets()
    {
        using var fixture = new ArchiveFixture();
        fixture.Mutate(db =>
        {
            for (var i = 0; i < 3; i++)
            {
                db.Usage.Add(new UsageDetail { RecordedAt = DateTimeOffset.UtcNow.AddDays(-200), RolledUp = true });
            }

            db.Add(new GoldpathArchiveEntry { Definition = "Claim", AggregateKey = "k", ChainIndex = 1, ArchivedAt = DateTimeOffset.UtcNow.AddDays(-10) });
        });

        var archival = new GoldpathArchivalOptions { BatchSize = 2 };
        archival.AddArchive<Claim>(a => a
            .Key(c => c.Id).DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value).RetainFor(years: 0));
        archival.AddRowRetention<UsageDetail>(r => r
            .After(TimeSpan.FromDays(90), u => u.RecordedAt).Where(u => u.RolledUp));

        var job = new GoldpathRetentionPurgeJob<ArchiveTestContext>(fixture.Engine, archival, TimeProvider.System);
        using var scope = fixture.Scope();
        var plan = await job.PlanAsync(CreateContext(scope.ServiceProvider), CancellationToken.None);

        Assert.Equal(["rows:UsageDetail|0", "rows:UsageDetail|1", "entries:Claim|0"], plan.ChunkPayloads);
        Assert.Equal(4, plan.TotalItems);
    }

    [Fact]
    public async Task Verify_job_plan_slices_after_the_purged_prefix()
    {
        using var fixture = new ArchiveFixture();
        fixture.Mutate(db => db.Add(new GoldpathArchiveChainState
        {
            Definition = "Claim",
            LastIndex = 1200,
            LastHash = "h",
            PurgedThroughIndex = 100,
            PurgedHeadHash = "p",
        }));

        var archival = new GoldpathArchivalOptions();
        archival.AddArchive<Claim>(a => a.Key(c => c.Id).DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value));

        var job = new GoldpathArchiveVerifyJob<ArchiveTestContext>(fixture.Engine, archival);
        using var scope = fixture.Scope();
        var plan = await job.PlanAsync(CreateContext(scope.ServiceProvider), CancellationToken.None);

        // 101..1200 in 500-slices: [101,600] [601,1100] [1101,1200]
        Assert.Equal(["Claim|101|600", "Claim|601|1100", "Claim|1101|1200"], plan.ChunkPayloads);
    }

    [Fact]
    public async Task Verify_of_a_middle_slice_anchors_on_the_prior_entry()
    {
        using var fixture = new ArchiveFixture();
        for (var i = 0; i < 4; i++)
        {
            fixture.SeedClaim();
        }

        var definition = ArchiveFixture.ClaimArchive();
        using (var scope = fixture.Scope())
        {
            await fixture.Engine.ArchiveNextBatchAsync(scope.ServiceProvider, definition, 10, CancellationToken.None);
        }

        using var verifyScope = fixture.Scope();
        Assert.Empty(await fixture.Engine.VerifySliceAsync(verifyScope.ServiceProvider, definition, 3, 4, CancellationToken.None));
    }

    private static GoldpathJobContext CreateContext(IServiceProvider services)
        => (GoldpathJobContext)Activator.CreateInstance(typeof(GoldpathJobContext),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [Guid.NewGuid(), "test", "node", "job", false, null, services], null)!;
}
