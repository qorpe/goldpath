using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Revision R3.4 (campaign RFC, #293): <c>MaxAwaitingAck</c> — the counter the sink keeps and the
/// leader re-counts, the pacer rule that splits live work from outstanding acknowledgements, the
/// throttle field and the gauge. With the field null every allowance is R2's, line for line.
/// </summary>
public class R3AwaitingAckTests
{
    private static GoldpathCampaignPolicy Policy(int maxInFlight, int? maxAwaitingAck)
        => new(10_000, null, maxInFlight, null, null, "UTC") { MaxAwaitingAck = maxAwaitingAck };

    private static GoldpathCampaign Ledger(long released, long succeeded, long failed, long awaitingAck)
        => new() { ReleasedThrough = released, SucceededCount = succeeded, FailedCount = failed, AwaitingAckCount = awaitingAck };

    // ---- the pacer rule (pure) ----

    [Fact]
    public void WithTheFieldNull_TheRoomIsR2s_ForEveryMixOfItemStates()
    {
        // Every split of up to 12 released items over the six states the ledger sees — Released,
        // Processing, AwaitingRetry, AwaitingAck, Succeeded, Failed — at three ceilings: R2's rule
        // counts all four non-terminal states, the acceptances included, whatever the counter says.
        var checkedMixes = 0;
        foreach (var maxInFlight in new[] { 1, 5, 12 })
        {
            for (var succeeded = 0; succeeded <= 12; succeeded++)
            {
                for (var failed = 0; succeeded + failed <= 12; failed++)
                {
                    for (var awaitingAck = 0; succeeded + failed + awaitingAck <= 12; awaitingAck++)
                    {
                        for (var live = 0; succeeded + failed + awaitingAck + live <= 12; live++)
                        {
                            var released = succeeded + failed + awaitingAck + live;
                            var r2 = maxInFlight - (released - succeeded - failed);
                            Assert.Equal(r2, GoldpathCampaignPacerJob<CampaignTestContext>.InFlightRoom(
                                Policy(maxInFlight, null), Ledger(released, succeeded, failed, awaitingAck)));
                            checkedMixes++;
                        }
                    }
                }
            }
        }

        Assert.True(checkedMixes > 1_000);
    }

    [Theory]
    // MaxInFlight 10, MaxAwaitingAck 50; in flight = live + acks. A release takes a slot under both
    // ceilings until its outcome says which one it stays under, so live work counts against both.
    [InlineData(0, 0, 10)]      // nothing out: MaxInFlight binds
    [InlineData(0, 40, 10)]     // forty acks outstanding do NOT take live slots (null would give -30)
    [InlineData(4, 40, 6)]      // four live calls take four of each
    [InlineData(0, 45, 5)]      // MaxAwaitingAck binds: five acks of room left
    [InlineData(2, 47, 1)]      // two live calls may still come back accepted: one release left, not three
    [InlineData(3, 50, 0)]      // at the ack ceiling nothing releases, whatever live room is left
    [InlineData(10, 0, 0)]      // at the live ceiling nothing releases, whatever ack room is left
    public void WithTheFieldSet_MaxInFlightCapsLiveWork_AndMaxAwaitingAckCapsTheAcceptances(long live, long acks, long room)
        => Assert.Equal(room, Math.Max(0, GoldpathCampaignPacerJob<CampaignTestContext>.InFlightRoom(
            Policy(10, 50), Ledger(released: 100 + live + acks, succeeded: 90, failed: 10, awaitingAck: acks))));

    [Fact]
    public void AWaitingRetry_KeepsItsLiveSlot()
    {
        // Owner decision Q2: AwaitingRetry is in flight and is not an acceptance, so it counts
        // against MaxInFlight exactly as in R2 — 3 retries waiting + 2 acks, MaxInFlight 4.
        Assert.Equal(1, GoldpathCampaignPacerJob<CampaignTestContext>.InFlightRoom(
            Policy(4, 100), Ledger(released: 5, succeeded: 0, failed: 0, awaitingAck: 2)));
    }

    [Theory]
    // Six in flight, MaxInFlight 10. Unclamped, -3 reads as nine live calls (room 1), and 99 as
    // -93 live calls (room 103, ten times MaxInFlight) until the next slice re-counts.
    [InlineData(-3, 1_000, 4)]
    [InlineData(99, 1_000, 10)]
    public void ADriftedCounter_IsHeldInsideWhatIsInFlight(long drifted, int maxAwaitingAck, long room)
        => Assert.Equal(room, GoldpathCampaignPacerJob<CampaignTestContext>.InFlightRoom(
            Policy(10, maxAwaitingAck), Ledger(released: 6, succeeded: 0, failed: 0, awaitingAck: drifted)));

    // ---- the counter: kept by the flush, re-counted by the leader ----

    private static int AwaitingAckRows(CampaignFixture fixture, Guid id)
        => fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Count(i => i.CampaignId == id && i.State == GoldpathCampaignItemState.AwaitingAck));

    [Fact]
    public async Task TheFlushCountsAcceptancesIn_AndSettlesFailuresAndTimeoutsOut()
    {
        using var fixture = R2Tests.ActionFixture(o => o.RetryJitter = 0, sourceSize: 5);
        for (var id = 1; id <= 5; id++)
        {
            fixture.Script[id] = GoldpathCampaignActionResult.Accepted($"corr-{id}", TimeSpan.FromMinutes(1));
        }

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var engine = new GoldpathCampaignEngine<CampaignTestContext>(
            fixture.Options, clock, NullLogger<GoldpathCampaignEngine<CampaignTestContext>>.Instance);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 2));
        await fixture.EnumerateAllAsync(campaign);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        await engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 5, CancellationToken.None);
        var outcomes = new List<GoldpathCampaignOutcomeMessage>();
        for (var seq = 1L; seq <= 5; seq++)
        {
            var item = await engine.ClaimAsync(db, campaign.Id, seq, CancellationToken.None);
            var result = await engine.ExecuteItemAsync(scope.ServiceProvider, "winback", campaign.Id, seq, item!.TargetJson, null,
                attempt: 1, replay: false, CancellationToken.None);
            outcomes.Add(engine.OutcomeFor(campaign.Id, seq, 1, result));
        }

        await engine.ApplyOutcomesAsync(db, campaign.Id, outcomes, CancellationToken.None);
        Assert.Equal(5, fixture.Reload(campaign.Id).AwaitingAckCount);

        // The same acceptances again (a redelivered batch): no row moves, so no count moves.
        await engine.ApplyOutcomesAsync(db, campaign.Id, outcomes, CancellationToken.None);
        Assert.Equal(5, fixture.Reload(campaign.Id).AwaitingAckCount);

        // A success callback settles one, a non-retryable failure another, a retryable one a third.
        await engine.ApplyOutcomesAsync(db, campaign.Id,
        [
            new GoldpathCampaignOutcomeMessage(campaign.Id, 1, true, null) { Attempt = 1 },
            new GoldpathCampaignOutcomeMessage(campaign.Id, 2, false, "refused") { Attempt = 1, Retryable = false },
            new GoldpathCampaignOutcomeMessage(campaign.Id, 3, false, "busy") { Attempt = 1, Retryable = true },
        ], CancellationToken.None);
        Assert.Equal(2, fixture.Reload(campaign.Id).AwaitingAckCount);

        // The ack sweep goes through the same flush: both remaining deadlines pass.
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(2, (await engine.SweepAckTimeoutsAsync(db, campaign, CancellationToken.None)).Count);
        var row = fixture.Reload(campaign.Id);
        Assert.Equal(0, row.AwaitingAckCount);
        Assert.Equal(AwaitingAckRows(fixture, campaign.Id), row.AwaitingAckCount);
        Assert.Equal(1, row.SucceededCount);
        Assert.Equal(1, row.FailedCount);
    }

    [Fact]
    public async Task AFailureStraightFromProcessing_MovesNoAcknowledgement()
    {
        // One flush: item 1 is accepted, item 2 fails while Processing (retryable, then terminal).
        // Only rows that LEAVE AwaitingAck may lower the count.
        using var fixture = R2Tests.ActionFixture(sourceSize: 3);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("corr-1", TimeSpan.FromHours(1));
        fixture.Script[2] = GoldpathCampaignActionResult.Failed("BUSY", "try later", retryable: true);
        fixture.Script[3] = GoldpathCampaignActionResult.Failed("NO", "never", retryable: false);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);
        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 3, CancellationToken.None);
        }

        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(1, AwaitingAckRows(fixture, campaign.Id));
        Assert.Equal(1, fixture.Reload(campaign.Id).AwaitingAckCount);
    }

    [Fact]
    public async Task ADriftedCounter_IsCorrectedAtTheNextSliceStart()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 3);
        for (var id = 1; id <= 3; id++)
        {
            fixture.Script[id] = GoldpathCampaignActionResult.Accepted($"corr-{id}", TimeSpan.FromHours(1));
        }

        var campaign = await fixture.CreateAsync(R2Tests.Generous() with { MaxAwaitingAck = 10 });
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();
        await fixture.ConsumeAllPublishedAsync();
        Assert.Equal(3, AwaitingAckRows(fixture, campaign.Id));

        // A flush that died between its item updates and its counter update leaves the cache wrong.
        fixture.Mutate(db => db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id).AwaitingAckCount = 42);
        await fixture.RunPacerSliceAsync();
        Assert.Equal(3, fixture.Reload(campaign.Id).AwaitingAckCount);

        // And the other way: a count driven below zero is re-counted up to the rows.
        fixture.Mutate(db => db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id).AwaitingAckCount = -7);
        await fixture.RunPacerSliceAsync();
        Assert.Equal(3, fixture.Reload(campaign.Id).AwaitingAckCount);
    }

    // ---- the pacer, wired: what the sink counts is what the next slice paces on ----

    [Theory]
    [InlineData(null, 2)]   // R2: two acceptances hold both MaxInFlight slots for as long as they wait
    [InlineData(5, 5)]      // R3.4: acceptances move to their own ceiling, and it stops the release at five
    public async Task AcceptancesStopTakingLiveSlots_AndStopAtTheirOwnCeiling(int? maxAwaitingAck, int expectedReleased)
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 8);
        for (var id = 1; id <= 8; id++)
        {
            fixture.Script[id] = GoldpathCampaignActionResult.Accepted($"slow-{id}", TimeSpan.FromHours(1));
        }

        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(10_000, null, 2, null, null, "UTC") { MaxAwaitingAck = maxAwaitingAck });
        await fixture.EnumerateAllAsync(campaign);

        // Each round: one slice of releases, then the fleet answers every one with an acceptance.
        for (var round = 0; round < 6; round++)
        {
            await fixture.RunPacerSliceAsync();
            await fixture.ConsumeAllPublishedAsync();
            Assert.InRange(AwaitingAckRows(fixture, campaign.Id), 0, maxAwaitingAck ?? int.MaxValue);   // never past the ceiling
        }

        var row = fixture.Reload(campaign.Id);
        Assert.Equal(expectedReleased, row.ReleasedThrough);
        Assert.Equal(expectedReleased, AwaitingAckRows(fixture, campaign.Id));
        Assert.Equal(expectedReleased, row.AwaitingAckCount);
    }

    // ---- the throttle and the detail ----

    [Fact]
    public async Task TheThrottlePatchesTheField_KeepsItWhenOmitted_ClearsIt_AndAuditsOldToNew()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var admin = fixture.Admin();
        var created = await admin.CreateAsync("winback", "acks", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 50) { MaxAwaitingAck = 500 }, tenant: null, "op", CancellationToken.None);
        Assert.True(created.Ok, created.Message);
        var id = fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);
        Assert.Equal(500, fixture.Reload(id).MaxAwaitingAck);

        Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle { MaxAwaitingAck = 2_000 }, "op", CancellationToken.None)).Ok);
        Assert.Equal(2_000, fixture.Reload(id).MaxAwaitingAck);

        Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(Tps: 60), "op", CancellationToken.None)).Ok);
        Assert.Equal(2_000, fixture.Reload(id).MaxAwaitingAck);   // omitted = keep

        foreach (var bad in new[] { 0, -1 })
        {
            var refused = await admin.ThrottleAsync(id, new GoldpathCampaignThrottle { MaxAwaitingAck = bad }, "op", CancellationToken.None);
            Assert.False(refused.Ok);
            Assert.Equal(2_000, fixture.Reload(id).MaxAwaitingAck);
        }

        Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle { ClearMaxAwaitingAck = true }, "op", CancellationToken.None)).Ok);
        Assert.Null(fixture.Reload(id).MaxAwaitingAck);

        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().AsNoTracking()
            .Where(a => a.CampaignId == id).OrderBy(a => a.Id).Select(a => a.Detail).ToList());
        Assert.Contains(audit, d => d!.Contains("maxAwaitingAck=500", StringComparison.Ordinal) && d.StartsWith("type=winback", StringComparison.Ordinal));
        Assert.Contains(audit, d => d!.Contains("maxAwaitingAck=500 -> ", StringComparison.Ordinal) && d.EndsWith("maxAwaitingAck=2000", StringComparison.Ordinal));
        Assert.Contains(audit, d => d!.Contains("maxAwaitingAck=2000 -> ", StringComparison.Ordinal) && d.EndsWith("maxAwaitingAck=none", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ANonPositiveCeiling_IsRefusedAtCreate(int ceiling)
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var refused = await fixture.Admin().CreateAsync("winback", "never releases", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 50) { MaxAwaitingAck = ceiling }, tenant: null, "op", CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("MaxAwaitingAck", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathCampaign>().Count()));
    }

    [Fact]
    public async Task ANonPositiveCeiling_InTheTypesDefault_IsRefusedAtCreateToo()
    {
        // The check reads the policy the campaign would run under, not only the patch: a default of zero, with no
        // patch or a patch without the field, still never releases.
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var type = fixture.Options.Type("winback");
        type.DefaultPolicy = type.DefaultPolicy with { MaxAwaitingAck = 0 };
        var admin = fixture.Admin();
        Assert.False((await admin.CreateAsync("winback", "no patch", new Dictionary<string, string>(), null, tenant: null, "op", CancellationToken.None)).Ok);
        Assert.False((await admin.CreateAsync("winback", "other field", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 50), tenant: null, "op", CancellationToken.None)).Ok);
        Assert.Equal(0, fixture.Query(db => db.Set<GoldpathCampaign>().Count()));
        // A patch that sets a positive ceiling over the bad default is the operator's fix, and is accepted.
        Assert.True((await admin.CreateAsync("winback", "fixed", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 50) { MaxAwaitingAck = 3 }, tenant: null, "op", CancellationToken.None)).Ok);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveCeiling_InADefaultPolicy_IsRefusedWhenTheTypeIsRegistered(int ceiling)
    {
        var options = new GoldpathCampaignOptions();
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => options.AddCampaign<TestTarget>("winback", c => c
            .MaxTargets(10).Targets((_, _) => NoTargets())
            .DefaultPolicy(p => p with { MaxAwaitingAck = ceiling })));
        Assert.Contains("MaxAwaitingAck", thrown.Message, StringComparison.Ordinal);
    }

    private static async IAsyncEnumerable<TestTarget> NoTargets()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Theory]
    [InlineData(-1, 0)]    // an upgraded row settled before the first slice's re-count
    [InlineData(5, 1)]     // a re-count racing a flush, above what is in flight
    public async Task TheDetail_HoldsADriftedCountInsideWhatIsInFlight(long stored, long shown)
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);   // one item, accepted: exactly one in flight
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("corr-1", TimeSpan.FromHours(1));
        var campaign = await fixture.CreateAsync(R2Tests.Generous() with { MaxAwaitingAck = 7 });
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();
        await fixture.ConsumeAllPublishedAsync();
        fixture.Mutate(db => db.Set<GoldpathCampaign>().Single(c => c.Id == campaign.Id).AwaitingAckCount = stored);

        var info = await fixture.Admin().GetAsync(campaign.Id, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(1, info.InFlight);
        Assert.Equal(shown, info.AwaitingAck);
    }

    [Fact]
    public async Task APolicyWithoutTheField_AuditsExactlyAsBefore()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var admin = fixture.Admin();
        Assert.True((await admin.CreateAsync("winback", "plain", new Dictionary<string, string>(),
            new GoldpathCampaignThrottle(Tps: 50), tenant: null, "op", CancellationToken.None)).Ok);
        var id = fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);
        Assert.True((await admin.ThrottleAsync(id, new GoldpathCampaignThrottle(Tps: 60), "op", CancellationToken.None)).Ok);
        var audit = fixture.Query(db => db.Set<GoldpathCampaignAudit>().AsNoTracking().Where(a => a.CampaignId == id).Select(a => a.Detail).ToList());
        Assert.All(audit, d => Assert.DoesNotContain("maxAwaitingAck", d!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDetailReturnsTheCeilingAndTheCount()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 2);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("corr-1", TimeSpan.FromHours(1));
        var campaign = await fixture.CreateAsync(R2Tests.Generous() with { MaxAwaitingAck = 7 });
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();
        await fixture.ConsumeAllPublishedAsync();

        var info = await fixture.Admin().GetAsync(campaign.Id, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(7, info.MaxAwaitingAck);
        Assert.Equal(1, info.AwaitingAck);
        Assert.Equal(1, info.InFlight);   // the acceptance is still in flight; the split is the pacer's
    }

    [Fact]
    public async Task TheAdminRoutes_BindMaxAwaitingAckAndItsClear_AndTheDetailCarriesTheCeilingAndTheCount()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 1);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<CampaignTestContext>(o => o.UseSqlite(fixture.Connection));
        builder.Services.AddSingleton(fixture.Admin());
        await using var app = builder.Build();
        app.MapGoldpathCampaignAdmin<CampaignTestContext>(exposeUnsecured: true);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var created = await client.PostAsJsonAsync("/goldpath/admin/campaign",
            JsonDocument.Parse("""{"type":"winback","name":"over http","policy":{"tps":50,"maxAwaitingAck":300}}""").RootElement);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = fixture.Query(db => db.Set<GoldpathCampaign>().AsNoTracking().Single().Id);

        var detail = JsonDocument.Parse(await client.GetStringAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}")));
        Assert.Equal(300, detail.RootElement.GetProperty("maxAwaitingAck").GetInt32());
        Assert.Equal(0, detail.RootElement.GetProperty("awaitingAck").GetInt64());

        var refused = await client.PostAsJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}/throttle"),
            JsonDocument.Parse("""{"maxAwaitingAck":0}""").RootElement);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Equal(300, fixture.Reload(id).MaxAwaitingAck);

        var cleared = await client.PostAsJsonAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}/throttle"),
            JsonDocument.Parse("""{"clearMaxAwaitingAck":true}""").RootElement);
        Assert.True(cleared.IsSuccessStatusCode, await cleared.Content.ReadAsStringAsync());
        Assert.Null(fixture.Reload(id).MaxAwaitingAck);
        detail = JsonDocument.Parse(await client.GetStringAsync(string.Create(CultureInfo.InvariantCulture, $"/goldpath/admin/campaign/{id}")));
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("maxAwaitingAck").ValueKind);
    }

    // ---- the gauge ----

    [Theory]
    [InlineData(-3, 2, 0)]
    [InlineData(9, 2, 2)]
    [InlineData(1, 2, 1)]
    public void TheAwaitingAckGauge_ReportsTheCountHeldInsideWhatIsInFlight(long stored, long inFlight, long reported)
    {
        var campaign = new GoldpathCampaign { Id = Guid.NewGuid(), Type = "winback", AwaitingAckCount = stored };
        GoldpathCampaignMetrics.Snapshot(campaign, inFlight);

        long? seen = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Goldpath.Campaign" && instrument.Name == "goldpath_campaign_awaiting_ack")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "campaign" && Equals(tag.Value, campaign.Id.ToString("N")))
                {
                    seen = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        Assert.Equal(reported, seen);
    }

    [Fact]
    public async Task TheAwaitingAckGauge_IsTaggedLikeTheInFlightGauge()
    {
        using var fixture = R2Tests.ActionFixture(sourceSize: 2);
        fixture.Script[1] = GoldpathCampaignActionResult.Accepted("corr-1", TimeSpan.FromHours(1));
        fixture.Script[2] = GoldpathCampaignActionResult.Accepted("corr-2", TimeSpan.FromHours(1));
        var campaign = await fixture.CreateAsync(R2Tests.Generous() with { MaxAwaitingAck = 7 });
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();
        await fixture.ConsumeAllPublishedAsync();
        await fixture.RunPacerSliceAsync();   // the slice re-reads the row and snapshots it

        var seen = new Dictionary<string, (long Value, Dictionary<string, object?> Tags)>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Goldpath.Campaign"
                && instrument.Name is "goldpath_campaign_awaiting_ack" or "goldpath_campaign_in_flight")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            if (Equals(map.GetValueOrDefault("campaign"), campaign.Id.ToString("N")))
            {
                seen[instrument.Name] = (value, map);
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        var awaiting = seen["goldpath_campaign_awaiting_ack"];
        Assert.Equal(2, awaiting.Value);
        Assert.Equal(seen["goldpath_campaign_in_flight"].Tags.Keys.Order(), awaiting.Tags.Keys.Order());
        Assert.Equal("winback", awaiting.Tags["type"]);
    }
}
