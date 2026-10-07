using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>Scripts a typed result per target id (R2.1); unscripted ids succeed.</summary>
public sealed class TestActionHandler(CampaignFixture fixture) : IGoldpathCampaignActionHandler<TestTarget>
{
    public static Dictionary<int, GoldpathCampaignActionResult> Script { get; } = [];

    public Task<GoldpathCampaignActionResult> ExecuteAsync(TestTarget target, GoldpathCampaignItemContext context, CancellationToken cancellationToken)
    {
        fixture.Executed.Add((target, context));
        return Task.FromResult(Script.TryGetValue(target.Id, out var scripted) ? scripted : GoldpathCampaignActionResult.Succeeded());
    }
}

/// <summary>Revision R2 (campaign RFC): typed results, acceptances + callbacks, the configurable jittered ladder.</summary>
public class R2Tests
{
    private static Action<GoldpathCampaignOptions> FastTicks(Action<GoldpathCampaignOptions>? extra = null)
        => o =>
        {
            o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
            o.LeaderTick = TimeSpan.FromMilliseconds(20);
            o.EnumerationBatchSize = 4;
            extra?.Invoke(o);
        };

    private static void ActionHandler(IServiceCollection services)
        => services.AddScoped<IGoldpathCampaignActionHandler<TestTarget>>(sp =>
            new TestActionHandler(sp.GetRequiredService<CampaignFixtureHolder>().Fixture));

    private static CampaignFixture ActionFixture(Action<GoldpathCampaignOptions>? extra = null, int sourceSize = 3)
    {
        CampaignFixture? fixture = null;
        fixture = new CampaignFixture(FastTicks(extra), sourceSize, services =>
        {
            services.AddSingleton(new CampaignFixtureHolder(() => fixture!));
            ActionHandler(services);
        });
        return fixture;
    }

    private static GoldpathCampaignPolicy Generous(int maxAttempts = 1)
        => new(10_000, null, 100, null, null, "UTC") { MaxAttempts = maxAttempts };

    // ---- R2.1 typed results ----

    [Fact]
    public async Task TheThreeResultShapes_LandInThreeItemStates()
    {
        TestActionHandler.Script.Clear();
        TestActionHandler.Script[2] = GoldpathCampaignActionResult.Failed("UNSUPPORTED_DEVICE", "no OMA-DM client", retryable: false);
        TestActionHandler.Script[3] = GoldpathCampaignActionResult.Accepted("corr-3", TimeSpan.FromMinutes(5));
        using var fixture = ActionFixture();
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var items = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().OrderBy(i => i.Seq).ToList());
        Assert.Equal(GoldpathCampaignItemState.Succeeded, items[0].State);

        // Non-retryable: terminal at ONCE despite attempts left, the code kept for the report.
        Assert.Equal(GoldpathCampaignItemState.Failed, items[1].State);
        Assert.Equal(1, items[1].Attempts);
        Assert.Equal("UNSUPPORTED_DEVICE", items[1].ErrorCode);
        Assert.Equal("no OMA-DM client", items[1].Error);
        Assert.Null(items[1].NextAttemptAt);

        // Accepted: parked, still in flight, deadline stamped from the handler's timeout.
        Assert.Equal(GoldpathCampaignItemState.AwaitingAck, items[2].State);
        Assert.Equal("corr-3", items[2].CorrelationId);
        Assert.NotNull(items[2].AckDeadline);
        Assert.InRange(items[2].AckDeadline!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(1, row.SucceededCount);
        Assert.Equal(1, row.FailedCount);
        Assert.Null(await fixture.Engine.TryCompleteAsync(
            scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), campaign.Id, CancellationToken.None));   // the acceptance blocks completion
    }

    [Fact]
    public async Task TheIdempotencyKeyNamesTheAttempt()
    {
        TestActionHandler.Script.Clear();
        using var fixture = ActionFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var (_, context) = Assert.Single(fixture.Executed);
        Assert.Equal(1, context.Attempt);
        Assert.Equal(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{campaign.Id:N}#1#1"), context.IdempotencyKey);
    }

    [Fact]
    public async Task ALegacyHandlerThrow_IsStillARetryableFailure()
    {
        using var fixture = new CampaignFixture(FastTicks(o => o.RetryJitter = 0), sourceSize: 1);
        fixture.FailIds.Add(1);
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 2));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var item = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);
        Assert.NotNull(item.NextAttemptAt);
        Assert.Equal(0, fixture.Reload(campaign.Id).FailedCount);
    }

    // ---- R2.1 callbacks and the ack sweep ----

    [Fact]
    public async Task ACallbackSettlesAnAcceptedItem_AndASecondCallbackIsIdempotent()
    {
        TestActionHandler.Script.Clear();
        TestActionHandler.Script[1] = GoldpathCampaignActionResult.Accepted("fota-42", TimeSpan.FromMinutes(30));
        using var fixture = ActionFixture(sourceSize: 1);
        var campaign = await fixture.CreateAsync(Generous());
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var unknown = await fixture.Engine.ResolveCallbackAsync(db, "winback", "nope", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.False(unknown.Found);

        var pending = await fixture.Engine.ResolveCallbackAsync(db, "winback", "fota-42", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.True(pending.Found);
        Assert.NotNull(pending.Outcome);
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [pending.Outcome!], CancellationToken.None);

        var item = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.Succeeded, item.State);
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);

        // The provider retries its webhook: found, already settled, nothing to publish.
        var repeat = await fixture.Engine.ResolveCallbackAsync(db, "winback", "fota-42", new GoldpathCampaignCallbackRequest(true), CancellationToken.None);
        Assert.True(repeat.Found);
        Assert.Null(repeat.Outcome);
        Assert.Equal(1, fixture.Reload(campaign.Id).SucceededCount);
    }

    [Fact]
    public async Task AFailedCallback_WalksTheLadder_WithTheProvidersCode()
    {
        TestActionHandler.Script.Clear();
        TestActionHandler.Script[1] = GoldpathCampaignActionResult.Accepted("sms-7", TimeSpan.FromMinutes(30));
        using var fixture = ActionFixture(o => o.RetryJitter = 0, sourceSize: 1);
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 2));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        await fixture.ConsumeAllPublishedAsync();

        var resolution = await fixture.Engine.ResolveCallbackAsync(db, "winback", "sms-7",
            new GoldpathCampaignCallbackRequest(false, "DELIVERY_FAILED", "handset unreachable", Retryable: true), CancellationToken.None);
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [resolution.Outcome!], CancellationToken.None);

        var item = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);
        Assert.Equal("DELIVERY_FAILED", item.ErrorCode);
        Assert.Null(item.CorrelationId);   // the acceptance is spent; the retry gets a fresh one
        Assert.Null(item.AckDeadline);
    }

    [Fact]
    public async Task AnAckDeadlineThatPasses_IsARetryableFailure_ThenExhausts()
    {
        TestActionHandler.Script.Clear();
        TestActionHandler.Script[1] = GoldpathCampaignActionResult.Accepted("slow-1", TimeSpan.FromMinutes(1));
        using var fixture = ActionFixture(o => o.RetryJitter = 0, sourceSize: 1);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 2));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);

        // The consumer, by hand, on the manual clock: claim → Accepted → park.
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        var result = await engine.ExecuteItemAsync(scope.ServiceProvider, "winback", campaign.Id, 1,
            fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).TargetJson, null, attempt: 1, replay: false, CancellationToken.None);
        await engine.ApplyOutcomesAsync(db, campaign.Id, [engine.OutcomeFor(campaign.Id, 1, result)], CancellationToken.None);
        Assert.Equal(GoldpathCampaignItemState.AwaitingAck, fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).State);

        // Before the deadline the sweep is silent; after it, attempt 1 is a RETRYABLE failure.
        Assert.Equal(0, await engine.SweepAckTimeoutsAsync(db, campaign, CancellationToken.None));
        clock.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await engine.SweepAckTimeoutsAsync(db, campaign, CancellationToken.None));
        var item = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);
        Assert.Equal("ACK_TIMEOUT", item.ErrorCode);
        // SQLite's binary DateTimeOffset converter rounds below the millisecond; the rung is what matters.
        Assert.NotNull(item.NextAttemptAt);
        Assert.InRange((item.NextAttemptAt!.Value - (clock.Now + TimeSpan.FromSeconds(30))).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0, fixture.Reload(campaign.Id).FailedCount);

        // Ripe → re-released → accepted again → deadline again → exhausted into the repair story.
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(1, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id, [engine.OutcomeFor(campaign.Id, 1, result)], CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, await engine.SweepAckTimeoutsAsync(db, campaign, CancellationToken.None));
        item = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.Failed, item.State);
        Assert.Equal(2, item.Attempts);
        Assert.Equal(1, fixture.Reload(campaign.Id).FailedCount);
    }

    // ---- R2.3 the configurable, jittered ladder ----

    [Fact]
    public async Task TheLadderIsConfigurable_AndJitterStaysInsideItsBand()
    {
        using var fixture = new CampaignFixture(FastTicks(o =>
        {
            o.RetryBackoff = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100)];
            o.RetryJitter = 0.2;
        }), sourceSize: 1);
        fixture.FailIds.Add(1);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);

        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id, [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, "flaky")], CancellationToken.None);
        var first = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).NextAttemptAt!.Value - clock.Now;
        Assert.InRange(first, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));

        clock.Now += TimeSpan.FromSeconds(13);
        Assert.Equal(1, await engine.ReleaseRipeRetriesAsync(scope.ServiceProvider, campaign, 10, CancellationToken.None));
        Assert.NotNull(await engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        await engine.ApplyOutcomesAsync(db, campaign.Id, [new GoldpathCampaignOutcomeMessage(campaign.Id, 1, false, "flaky")], CancellationToken.None);
        var second = fixture.Query(d => d.Set<GoldpathCampaignItem>().AsNoTracking().Single()).NextAttemptAt!.Value - clock.Now;
        Assert.InRange(second, TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void JitterSpreadsAThousandFailuresInsteadOfStackingThem()
    {
        var options = new GoldpathCampaignOptions { RetryJitter = 0.2 };
        var delays = Enumerable.Range(0, 1_000).Select(_ => options.NextRetryDelay(1)).ToArray();
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromSeconds(24), TimeSpan.FromSeconds(36)));
        Assert.True(delays.Distinct().Count() > 100, "jitter must actually vary");

        var pinned = new GoldpathCampaignOptions { RetryJitter = 0 };
        Assert.Equal(TimeSpan.FromSeconds(30), pinned.NextRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(2), pinned.NextRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(10), pinned.NextRetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(10), pinned.NextRetryDelay(9));   // the last rung repeats
    }

    [Fact]
    public void ResultFactoriesRefuseEmptyCodesAndNonPositiveTimeouts()
    {
        Assert.Throws<ArgumentException>(() => GoldpathCampaignActionResult.Failed("", "x", true));
        Assert.Throws<ArgumentException>(() => GoldpathCampaignActionResult.Accepted(" ", TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => GoldpathCampaignActionResult.Accepted("c", TimeSpan.Zero));
    }

    // ---- the callback surface ----

    [Fact]
    public void The_callback_surface_is_one_route_outside_the_admin_prefix()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite("DataSource=:memory:"));
        builder.Services.AddSingleton(new GoldpathCampaignOptions());
        using var app = builder.Build();
        app.MapGoldpathCampaignCallbacks<CampaignTestContext>();

        var actual = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single()} {e.RoutePattern.RawText}")
            .ToArray();
        Assert.Equal(["POST /goldpath/campaign/callbacks/{type}/{correlationId}"], actual);
    }
}

/// <summary>Lets the scripted action handler reach the fixture it is registered into.</summary>
public sealed class CampaignFixtureHolder(Func<CampaignFixture> resolve)
{
    public CampaignFixture Fixture => resolve();
}
