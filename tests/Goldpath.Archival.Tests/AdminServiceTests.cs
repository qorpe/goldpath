using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Archival.Tests;

/// <summary>A protector fake: Verdict/Name are classified; redaction is a fixed token.</summary>
public sealed class FakeProtector : IGoldpathDataProtector
{
    public IReadOnlyCollection<string> CatalogedPropertyNames => ["Verdict", "Name"];

    public bool IsClassified(Type declaringType, string propertyName)
        => propertyName is "Verdict" or "Name";

    public string? Redact(Type declaringType, string propertyName, string? value)
        => value is null ? null : "***";
}

public class AdminServiceTests : IDisposable
{
    private readonly ArchiveFixture _fixture = new();
    private readonly GoldpathArchivalOptions _options;
    private readonly GoldpathArchiveAdminService<ArchiveTestContext> _admin;
    private readonly ServiceProvider _withProtector;

    public AdminServiceTests()
    {
        _options = new GoldpathArchivalOptions();
        _options.AddArchive<Claim>(a => a
            .Graph(c => c.Decisions, c => c.Documents)
            .Key(c => c.Id)
            .DueWhen(c => c.ClosedAt != null, c => c.ClosedAt!.Value)
            .ArchiveAfter(TimeSpan.FromDays(30))
            .RetainFor(years: 0)
            .Tenant(c => c.TenantId)
            .DeleteHotRowsAfterArchive());

        // A sibling provider on the SAME database that also carries the protector —
        // the erasure path resolves IGoldpathDataProtector from its scope.
        _withProtector = BuildProviderSharingDb();

        _admin = new GoldpathArchiveAdminService<ArchiveTestContext>(
            _fixture.Engine, _options,
            _withProtector.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);
    }

    private ServiceProvider BuildProviderSharingDb()
    {
        var fresh = new ServiceCollection();
        fresh.AddSingleton<IGoldpathDataProtector, FakeProtector>();
        fresh.AddDbContext<ArchiveTestContext>(o => o.UseSqlite(_fixture.Connection));
        fresh.AddSingleton(TimeProvider.System);
        return fresh.BuildServiceProvider();
    }

    public void Dispose()
    {
        _withProtector.Dispose();
        _fixture.Dispose();
    }

    private async Task<string> ArchiveOneAsync(string? tenant = "agency-1")
    {
        var claim = _fixture.SeedClaim(tenant: tenant);
        using var scope = _fixture.Scope();
        await _fixture.Engine.ArchiveNextBatchAsync(scope.ServiceProvider, _options.Archives[0], 10, CancellationToken.None);
        return claim.Id.ToString();
    }

    [Fact]
    public async Task Definitions_view_reports_live_numbers()
    {
        await ArchiveOneAsync();
        _fixture.SeedClaim();   // due, not yet archived

        var status = Assert.Single(await _admin.GetDefinitionsAsync(CancellationToken.None));
        Assert.Equal("Claim", status.Name);
        Assert.Equal(1, status.Entries);
        Assert.Equal(1, status.DueBacklog);
        Assert.Equal(0, status.ActiveHolds);
        Assert.Equal(1, status.ChainHead);
    }

    [Fact]
    public async Task Retrieval_is_tenant_scoped()
    {
        var key = await ArchiveOneAsync(tenant: "agency-1");

        Assert.NotNull(await _admin.RetrieveAsync("Claim", key, null, CancellationToken.None));
        Assert.NotNull(await _admin.RetrieveAsync("Claim", key, "agency-1", CancellationToken.None));
        Assert.Null(await _admin.RetrieveAsync("Claim", key, "agency-2", CancellationToken.None));   // fail-closed
    }

    [Fact]
    public async Task Hold_lifecycle_is_evidence_and_blocks_erasure_and_purge()
    {
        var key = await ArchiveOneAsync();

        var noCase = await _admin.PlaceHoldAsync("Claim", key, "", "counsel", CancellationToken.None);
        Assert.False(noCase.Ok);   // no case, no hold
        Assert.Contains("case reference", noCase.Message, StringComparison.Ordinal);
        var placed = await _admin.PlaceHoldAsync("Claim", key, "LIT-1", "counsel", CancellationToken.None);
        Assert.True(placed.Ok);
        Assert.Equal("hold placed", placed.Message);
        var duplicate = await _admin.PlaceHoldAsync("Claim", key, "LIT-2", "counsel", CancellationToken.None);
        Assert.False(duplicate.Ok);   // already held
        Assert.Contains("already covers", duplicate.Message, StringComparison.Ordinal);

        var erase = await _admin.EraseAsync("Claim", key, "subject-x", "dpo", null, CancellationToken.None);
        Assert.False(erase.Ok);
        Assert.Contains("legal hold", erase.Message, StringComparison.Ordinal);

        using (var scope = _fixture.Scope())
        {
            Assert.Equal(0, await _fixture.Engine.PurgeExpiredEntriesAsync(scope.ServiceProvider, _options.Archives[0], 10, CancellationToken.None));
        }

        var hold = Assert.Single(await _admin.GetHoldsAsync(includeLifted: false, 10, CancellationToken.None));
        Assert.Equal("LIT-1", hold.CaseReference);
        Assert.Equal("counsel", hold.PlacedBy);

        var lifted = await _admin.LiftHoldAsync("Claim", key, "counsel", CancellationToken.None);
        Assert.True(lifted.Ok);
        Assert.Equal("hold lifted", lifted.Message);
        Assert.Empty(await _admin.GetHoldsAsync(includeLifted: false, 10, CancellationToken.None));
        var history = Assert.Single(await _admin.GetHoldsAsync(includeLifted: true, 10, CancellationToken.None));
        Assert.Equal("counsel", history.LiftedBy);
        Assert.NotNull(history.LiftedAt);
        var reLift = await _admin.LiftHoldAsync("Claim", key, "counsel", CancellationToken.None);
        Assert.False(reLift.Ok);
        Assert.Contains("no active hold", reLift.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_targets_fail_with_teaching_messages()
    {
        await ArchiveOneAsync();
        Assert.Contains("no archive definition",
            (await _admin.EraseAsync("Nope", "k", "s", "dpo", null, CancellationToken.None)).Message, StringComparison.Ordinal);
        Assert.Contains("no such archive entry",
            (await _admin.EraseAsync("Claim", "missing-key", "s", "dpo", null, CancellationToken.None)).Message, StringComparison.Ordinal);
        Assert.Null(await _admin.RetrieveAsync("Claim", "missing-key", null, CancellationToken.None));
    }

    [Fact]
    public async Task Erasure_redacts_classified_fields_restamps_content_and_keeps_the_chain_verifiable()
    {
        var key = await ArchiveOneAsync();

        var result = await _admin.EraseAsync("Claim", key, "subject-x", "dpo", "ticket KVKK-7", CancellationToken.None);
        Assert.True(result.Ok, result.Message);
        Assert.Contains("classified fields redacted", result.Message, StringComparison.Ordinal);

        var entry = _fixture.Query(db => db.Set<GoldpathArchiveEntry>().Single());
        Assert.NotNull(entry.ErasedAt);
        Assert.DoesNotContain("verdict-0", entry.Document, StringComparison.Ordinal);   // classified → gone
        Assert.Contains("***", entry.Document, StringComparison.Ordinal);
        Assert.Contains("CLM-", entry.Document, StringComparison.Ordinal);              // unclassified survives
        // Content hash re-stamped over the redacted document; chain hash untouched.
        Assert.Equal(GoldpathArchiveEnvelope.ContentHash(entry.SchemaVersion, entry.Definition, entry.AggregateKey, entry.Tenant, entry.DueAt, entry.Document), entry.ContentHash)
            ;
        Assert.NotEqual(entry.ContentHash, entry.ChainHash);

        // THE rule: erased divergence is LAWFUL — verification stays clean.
        using var scope = _fixture.Scope();
        Assert.Empty(await _fixture.Engine.VerifySliceAsync(scope.ServiceProvider, _options.Archives[0], 1, 1, CancellationToken.None));

        // The evidence row is the KVKK answer.
        var record = Assert.Single(await _admin.GetErasuresAsync(10, CancellationToken.None));
        Assert.Equal("subject-x", record.SubjectKey);
        Assert.Equal("dpo", record.RequestedBy);
        Assert.Equal(1, record.EntriesAffected);
        Assert.Equal("ticket KVKK-7", record.Detail);

        // A second erasure finds nothing left to redact.
        Assert.False((await _admin.EraseAsync("Claim", key, "subject-x", "dpo", null, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task A2_tamper_hidden_behind_ErasedAt_is_caught()
    {
        var key = await ArchiveOneAsync();
        Assert.True((await _admin.EraseAsync("Claim", key, "subject-x", "dpo", null, CancellationToken.None)).Ok);

        // The audit's attack: a table-writer alters the document, re-stamps BOTH hashes
        // and hides behind ErasedAt. Pre-A2, verification skipped erased rows entirely.
        _fixture.Mutate(db =>
        {
            var entry = db.Set<GoldpathArchiveEntry>().Single();
            entry.Document = entry.Document.Replace("***", "$$$", StringComparison.Ordinal);
            entry.ContentHash = GoldpathArchiveEnvelope.ContentHash(
                entry.SchemaVersion, entry.Definition, entry.AggregateKey, entry.Tenant, entry.DueAt, entry.Document);
            entry.PreErasureContentHash = entry.ContentHash;   // even the retained hash is forged
        });

        using var scope = _fixture.Scope();
        var findings = await _fixture.Engine.VerifySliceAsync(scope.ServiceProvider, _options.Archives[0], 1, 1, CancellationToken.None);
        Assert.Contains(findings, f => f.Problem.Contains("sealed chain hash", StringComparison.Ordinal));

        // Deleting the retained hash is no escape either — that is its own finding.
        _fixture.Mutate(db => db.Set<GoldpathArchiveEntry>().Single().PreErasureContentHash = null);
        using var scope2 = _fixture.Scope();
        var findings2 = await _fixture.Engine.VerifySliceAsync(scope2.ServiceProvider, _options.Archives[0], 1, 1, CancellationToken.None);
        Assert.Contains(findings2, f => f.Problem.Contains("no pre-erasure content hash", StringComparison.Ordinal));
    }

    /// <summary>
    /// #476: a blank subject key is refused in words before anything is redacted or recorded, the same on every provider
    /// (Oracle stores '' as NULL, and the NOT NULL column refused it with ORA-01400 as a 500). A blank case reference on a
    /// legal hold is refused the same way; before, only the empty string was.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(null)]
    public async Task A_blank_subject_key_or_case_reference_is_refused_before_anything_is_written(string? blank)
    {
        var key = await ArchiveOneAsync();

        var erase = await _admin.EraseAsync("Claim", key, blank!, "dpo", null, CancellationToken.None);
        Assert.False(erase.Ok);
        Assert.Contains("data subject's key", erase.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Query(db => db.Set<GoldpathErasureRecord>().Count()));
        Assert.Null(_fixture.Query(db => db.Set<GoldpathArchiveEntry>().Single()).ErasedAt);   // nothing redacted either

        var hold = await _admin.PlaceHoldAsync("Claim", key, blank!, "counsel", CancellationToken.None);
        Assert.False(hold.Ok);
        Assert.Contains("case reference", hold.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Query(db => db.Set<GoldpathLegalHold>().Count()));
    }

    [Fact]
    public async Task Erasure_refuses_without_the_dataprotection_module()
    {
        var key = await ArchiveOneAsync();
        var bare = new GoldpathArchiveAdminService<ArchiveTestContext>(
            _fixture.Engine, _options,
            _fixture.Services.GetRequiredService<IServiceScopeFactory>(),   // no protector here
            TimeProvider.System);

        var result = await bare.EraseAsync("Claim", key, "s", "dpo", null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("GP1401", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_demand_verify_reports_tamper()
    {
        var key = await ArchiveOneAsync();
        _fixture.Mutate(db =>
        {
            var entry = db.Set<GoldpathArchiveEntry>().Single();
            entry.Document += " ";
        });

        var findings = await _admin.VerifyAsync("Claim", CancellationToken.None);
        Assert.NotEmpty(findings);
        Assert.Empty(await _admin.VerifyAsync("NoSuchDefinition", CancellationToken.None));
        _ = key;
    }
}
