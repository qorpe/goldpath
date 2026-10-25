using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// G1 Part 3/4 (RFC goldpath-messaging-engines §4.1 rows 11–12): the item handler and the outcome batch
/// handler on the seam keep the consumers' behaviour — claim before execute (constraint 2), the outcome
/// published rather than written, a thrown handler a retryable failure, and the sink's set-based write per
/// campaign (constraint 4).
/// </summary>
public class HandlerTests
{
    private sealed class LongFailure : IGoldpathCampaignItemHandler<TestTarget>
    {
        public Task ExecuteAsync(TestTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException(new string('x', 1500));
    }

    private sealed class Cancelled : IGoldpathCampaignItemHandler<TestTarget>
    {
        public Task ExecuteAsync(TestTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
            => throw new OperationCanceledException("the host is stopping");
    }

    private static IntegrationEventContext Delivery() => new(Guid.NewGuid(), null, null, 0, new Dictionary<string, object?>());

    private static GoldpathCampaignItemHandler<CampaignTestContext> ItemHandler(CampaignFixture fixture, IServiceScope scope)
        => new(fixture.Engine, scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), scope.ServiceProvider,
            fixture.Publisher, NullLogger<GoldpathCampaignItemHandler<CampaignTestContext>>.Instance);

    private static async Task<List<GoldpathCampaignItemMessage>> ReleasedAsync(CampaignFixture fixture, int count)
    {
        var campaign = await fixture.CreateAsync();
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, fixture.Reload(campaign.Id), count, CancellationToken.None);
        var released = fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().Where(m => m.CampaignId == campaign.Id).ToList();
        Assert.Equal(count, released.Count);
        return released;
    }

    [Fact]
    public async Task A_released_item_is_executed_once_and_its_outcome_published_and_a_redelivery_is_dropped()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        var message = Assert.Single(await ReleasedAsync(fixture, 1));

        using (var scope = fixture.Services.CreateScope())
        {
            await ItemHandler(fixture, scope).HandleAsync(message, Delivery());
        }

        var outcome = Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
        Assert.Equal((message.CampaignId, message.Seq, true, 1), (outcome.CampaignId, outcome.Seq, outcome.Succeeded, outcome.Attempt));
        Assert.False(Assert.Single(fixture.Executed).Context.Replay);   // a delivery, not the replay verb

        // The broker delivers the same coordinate again: the claim finds nothing to take.
        using (var scope = fixture.Services.CreateScope())
        {
            await ItemHandler(fixture, scope).HandleAsync(message, Delivery());
        }

        Assert.Single(fixture.Executed);
        Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
    }

    [Fact]
    public async Task A_throwing_handler_becomes_a_retryable_outcome_carrying_its_error()
    {
        using var fixture = new CampaignFixture(sourceSize: 1);
        fixture.FailIds.Add(1);
        var message = Assert.Single(await ReleasedAsync(fixture, 1));

        using (var scope = fixture.Services.CreateScope())
        {
            await ItemHandler(fixture, scope).HandleAsync(message, Delivery());
        }

        var outcome = Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
        Assert.False(outcome.Succeeded);
        Assert.Equal("HANDLER_EXCEPTION", outcome.ErrorCode);
        Assert.True(outcome.Retryable);
        Assert.Equal(1, outcome.Attempt);
        Assert.Equal("the provider refused target 1", outcome.Error);
    }

    [Fact]
    public async Task A_long_error_is_cut_at_a_thousand_characters()
    {
        using var fixture = new CampaignFixture(sourceSize: 1, extraServices: s => s.AddScoped<IGoldpathCampaignItemHandler<TestTarget>, LongFailure>());
        var message = Assert.Single(await ReleasedAsync(fixture, 1));

        using (var scope = fixture.Services.CreateScope())
        {
            await ItemHandler(fixture, scope).HandleAsync(message, Delivery());
        }

        var outcome = Assert.Single(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());
        Assert.Equal(new string('x', 1000), outcome.Error);
    }

    [Fact]
    public async Task A_cancelled_handler_is_not_reported_as_an_outcome()
    {
        using var fixture = new CampaignFixture(sourceSize: 1, extraServices: s => s.AddScoped<IGoldpathCampaignItemHandler<TestTarget>, Cancelled>());
        var message = Assert.Single(await ReleasedAsync(fixture, 1));

        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ItemHandler(fixture, scope).HandleAsync(message, Delivery()));

        Assert.Empty(fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>());   // the delivery fails back to the broker instead
    }

    [Fact]
    public async Task The_sink_applies_one_batch_of_outcomes_from_two_campaigns_each_to_its_own()
    {
        using var fixture = new CampaignFixture(sourceSize: 3);
        fixture.FailIds.Add(3);
        var first = await ReleasedAsync(fixture, 3);
        var second = await ReleasedAsync(fixture, 3);
        foreach (var message in first.Concat(second))
        {
            using var scope = fixture.Services.CreateScope();
            await ItemHandler(fixture, scope).HandleAsync(message, Delivery());
        }

        var batch = fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>()
            .Select(o => new IntegrationEvent<GoldpathCampaignOutcomeMessage>(o, Delivery()))
            .ToList();
        Assert.Equal(6, batch.Count);
        using (var scope = fixture.Services.CreateScope())
        {
            await new GoldpathCampaignOutcomeHandler<CampaignTestContext>(fixture.Engine, scope.ServiceProvider.GetRequiredService<CampaignTestContext>())
                .HandleAsync(batch);
        }

        foreach (var campaignId in new[] { first[0].CampaignId, second[0].CampaignId })
        {
            var campaign = fixture.Reload(campaignId);
            Assert.Equal((2, 1), (campaign.SucceededCount, campaign.FailedCount));
        }
    }
}
