using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// The R2 review's findings (2026-10-20), each turned into the regression test that asserts
/// the CORRECT behaviour. Every test names the finding and the rule it proves.
/// </summary>
public class R2ReviewTests
{
    // ---- finding 5: a replay heals the ledger on its OUTCOME, never before (RFC D5 "repair, not requeue") ----

    /// <summary>
    /// Finding 5 / RFC D5: a replay whose handler throws leaves the item Failed and the
    /// failed count where it was — re-driving it twice must not drift the ledger.
    /// </summary>
    [Fact]
    public async Task AReplayWhoseHandlerThrows_LeavesTheItemFailed_AndTheLedgerTrue()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        fixture.FailIds.Add(1);
        var campaign = await FailOneItemAsync(fixture);

        var key = $"{campaign.Id:N}#1";
        for (var i = 0; i < 2; i++)
        {
            using var scope = fixture.Services.CreateScope();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Engine.ReplayItemAsync(scope.ServiceProvider, key, CancellationToken.None));
        }

        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        Assert.Equal(GoldpathCampaignItemState.Failed, SingleItem(fixture).State);

        // The provider recovered: the next replay heals the ledger exactly once.
        fixture.FailIds.Clear();
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, key, CancellationToken.None);
        }

        var healed = fixture.Reload(campaign.Id);
        Assert.Equal(0, healed.FailedCount);
        Assert.Equal(1, healed.SucceededCount);
        Assert.Equal(GoldpathCampaignItemState.Succeeded, SingleItem(fixture).State);
    }

    /// <summary>
    /// Finding 5 / constraint 2 (claim before execute): a replay never steals an item a
    /// consumer owns — only a FAILED item can be re-claimed by the repair verb.
    /// </summary>
    [Fact]
    public async Task AReplayNeverStealsAnItemSomeoneElseOwns()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));   // a consumer is mid-call

        await fixture.Engine.ReplayItemAsync(scope.ServiceProvider, $"{campaign.Id:N}#1", CancellationToken.None);

        Assert.Empty(fixture.Executed);
        Assert.Equal(GoldpathCampaignItemState.Processing, SingleItem(fixture).State);
        Assert.Equal(0, fixture.Reload(campaign.Id).FailedCount);
    }

    // ---- helpers ----

    private static async Task<GoldpathCampaign> FailOneItemAsync(CampaignFixture fixture)
    {
        var campaign = await fixture.CreateAsync(R2Tests.Generous());
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), 1, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
        return campaign;
    }

    private static GoldpathCampaignItem SingleItem(CampaignFixture fixture)
        => fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single());
}
