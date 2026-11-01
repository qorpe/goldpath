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

    private static Microsoft.EntityFrameworkCore.Metadata.IModel BuildModel()
    {
        var builder = new ModelBuilder(Microsoft.EntityFrameworkCore.Metadata.Conventions.SqliteConventionSetBuilder.Build());
        builder.AddGoldpathArchiveModel();
        return builder.FinalizeModel();
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
