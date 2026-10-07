using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>Revision R2.7: a keyset selector resumes AFTER the last materialized key, never by re-reading and skipping.</summary>
public class R2KeysetTests
{
    private static Action<GoldpathCampaignOptions> Keyed(int count, List<string?> openings)
        => o =>
        {
            o.EnumerationBatchSize = 4;
            o.AddCampaign<TestTarget>("keyed", c => c
                .MaxTargets(10_000)
                .TargetsAfter((_, _, after) =>
                {
                    openings.Add(after);
                    return After(count, after);
                }, target => target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        };

    [Fact]
    public async Task ATakeoverReopensAfterTheLastKey_WithNoSkipRead()
    {
        var openings = new List<string?>();
        using var fixture = new CampaignFixture(Keyed(10, openings));
        var campaign = await CreateKeyedAsync(fixture);
        Assert.True(fixture.Options.Type("keyed").ResumesByKey);

        // Leader A: one step (4 items), then it dies — its stream goes with it.
        using (var scope = fixture.Services.CreateScope())
        {
            var stream = await fixture.Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
            await fixture.Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None);
            await stream.DisposeAsync();
        }

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(4, row.EnumeratedThrough);
        Assert.Equal("4", row.EnumeratedKey);   // the key rode the same write as the items

        // Leader B takes over from the row: the selector opens AFTER "4" — not from the
        // start with four rows skipped.
        await fixture.EnumerateAllAsync(row);
        Assert.Equal([null, "4"], openings);

        var done = fixture.Reload(campaign.Id);
        Assert.True(done.EnumerationComplete);
        Assert.Equal(10, done.EnumeratedThrough);
        Assert.Equal("10", done.EnumeratedKey);
        var items = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().OrderBy(i => i.Seq).ToList());
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), items.Select(i => i.Seq));   // dense, no gap, no repeat
        Assert.Equal(10, items.Select(i => i.TargetJson).Distinct().Count());
    }

    [Fact]
    public async Task ACountTypeStillSkipsByCount_AndCarriesNoKey()
    {
        using var fixture = new CampaignFixture(o => o.EnumerationBatchSize = 4, sourceSize: 6);
        var campaign = await fixture.CreateAsync();
        Assert.False(fixture.Options.Type("winback").ResumesByKey);
        await fixture.EnumerateAllAsync(campaign);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(6, row.EnumeratedThrough);
        Assert.Null(row.EnumeratedKey);
    }

    [Fact]
    public void BothSelectorsOnOneType_IsRefused()
    {
        var options = new GoldpathCampaignOptions();
        var error = Assert.Throws<InvalidOperationException>(() => options.AddCampaign<TestTarget>("both", c => c
            .MaxTargets(10)
            .Targets((_, _) => After(1, null))
            .TargetsAfter((_, _, after) => After(1, after), t => "k")));
        Assert.Contains("pick one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedKeyIsRefused_BeforeAnythingIsWritten()
    {
        using var fixture = new CampaignFixture(o =>
            o.AddCampaign<TestTarget>("longkey", c => c
                .MaxTargets(10)
                .TargetsAfter((_, _, after) => After(2, after), _ => new string('k', 257))));
        using var scope = fixture.Services.CreateScope();
        var campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, "longkey", "x",
            new Dictionary<string, string>(), null, tenant: null, actor: "tester", CancellationToken.None);
        var stream = await fixture.Engine.OpenStreamAtWatermarkAsync(scope.ServiceProvider, campaign, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Engine.EnumerateStepAsync(scope.ServiceProvider, campaign, stream, CancellationToken.None));
        await stream.DisposeAsync();
        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathCampaignItem>().Count()));
    }

    private static async Task<GoldpathCampaign> CreateKeyedAsync(CampaignFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return await fixture.Engine.CreateAsync(scope.ServiceProvider, "keyed", "keyed run",
            new Dictionary<string, string>(), new GoldpathCampaignPolicy(10_000, null, 100, null, null, "UTC"),
            tenant: null, actor: "tester", CancellationToken.None);
    }

    /// <summary>A keyset source: ids strictly greater than <paramref name="after"/>, ascending.</summary>
    private static async IAsyncEnumerable<TestTarget> After(int count, string? after)
    {
        var from = after is null ? 1 : int.Parse(after, System.Globalization.CultureInfo.InvariantCulture) + 1;
        for (var i = from; i <= count; i++)
        {
            yield return new TestTarget(i, $"keyed{i}@example.test");
        }

        await Task.CompletedTask;
    }
}
