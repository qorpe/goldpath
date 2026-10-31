using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// RFC §7 is a contract: every instrument the <c>Goldpath.Campaign</c> meter exports — its
/// name, unit and tag keys — asserted through a real <see cref="MeterListener"/> while a real
/// pacer slice, item handler and sink run (#294). A renamed, silenced or re-tagged metric dies
/// here, not on a blind dashboard.
/// </summary>
public class MetricsExportTests
{
    /// <summary>Every instrument the meter exports: unit (null = none) and tag keys, as RFC §7 lists them.</summary>
    private static readonly Dictionary<string, (string? Unit, string[] Tags)> Contract = new(StringComparer.Ordinal)
    {
        ["goldpath_campaign_released_total"] = (null, ["type"]),
        ["goldpath_campaign_succeeded_total"] = (null, ["campaign"]),
        ["goldpath_campaign_failed_total"] = (null, ["campaign"]),
        ["goldpath_campaign_window_closed_ticks_total"] = (null, ["type"]),
        ["goldpath_campaign_ack_timeouts_total"] = (null, ["type"]),
        ["goldpath_campaign_orphans_rereleased_total"] = (null, ["type"]),
        ["goldpath_campaign_callbacks_parked_total"] = (null, ["type"]),
        ["goldpath_campaign_callbacks_rejected_total"] = (null, ["type"]),
        ["goldpath_campaign_outcomes_total"] = (null, ["type", "outcome", "error_code"]),
        ["goldpath_campaign_handler_duration"] = ("s", ["type"]),
        ["goldpath_campaign_sink_flush_lag"] = ("s", ["type"]),
        ["goldpath_campaign_in_flight"] = (null, ["campaign", "type"]),
        ["goldpath_campaign_remaining"] = (null, ["campaign", "type"]),
        ["goldpath_campaign_tps_ceiling"] = (null, ["campaign", "type"]),
        ["goldpath_campaign_type_max_tps"] = (null, ["type"]),
        ["goldpath_campaign_global_tps"] = (null, []),
        ["goldpath_campaign_binding_constraint"] = (null, ["campaign", "type", "constraint"]),
        ["goldpath_campaign_released_today"] = (null, ["campaign", "type"]),
        ["goldpath_campaign_daily_quota"] = (null, ["campaign", "type"]),
        ["goldpath_campaign_leader_last_tick"] = ("s", []),
    };

    /// <summary>The type this class registers — no other test uses it, so its type-tagged series are this class's alone.</summary>
    private const string ProbeType = "metricsprobe";

    /// <summary>A GlobalTps no other test configures, so its measurement is recognisably this test's.</summary>
    private const int ProbeGlobalTps = 4321;

    [Fact]
    public async Task EveryInstrument_IsExported_WithItsNameUnitAndTags()
    {
        RuntimeHelpers.RunClassConstructor(typeof(GoldpathCampaignMetrics).TypeHandle);
        using var probe = new MetricsProbe();
        var startedAt = DateTimeOffset.UtcNow;

        using var fixture = new CampaignFixture(o =>
        {
            Fast(o);
            o.GlobalTps = ProbeGlobalTps;
            o.AddCampaign<TestTarget>(ProbeType, c => c.MaxTargets(100).MaxTps(777).Targets((_, _) => Targets(4)));
        });
        fixture.FailIds.Add(3);
        // Two attempts, so the item that throws walks the ladder: its outcome is a retry.
        var campaign = await CreateAsync(fixture, new GoldpathCampaignPolicy(1_000, 50, 100, null, null, "UTC") { MaxAttempts = 2 });
        await fixture.RunPacerSliceAsync();
        probe.Listener.RecordObservableInstruments();

        // The fleet, through the shipped handler and sink: one item throws, so its outcome carries HANDLER_EXCEPTION.
        await RunHandlerAndSinkAsync(fixture);

        // The signals this scenario cannot reach are emitted through the same helpers their call sites use (its one
        // failure is a retry, so nothing is terminally failed); their wiring is proven by R2CallbackTests,
        // R2ReviewTests, the pacer suites and TheOutcomesCounter_CountsRowsMoved_NotMessagesReceived.
        GoldpathCampaignMetrics.Outcomes(campaign.Id, 0, 1);
        GoldpathCampaignMetrics.AckTimeouts(ProbeType, 1);
        GoldpathCampaignMetrics.Orphans(ProbeType, 1);
        GoldpathCampaignMetrics.CallbacksParked(ProbeType, 1);
        GoldpathCampaignMetrics.CallbacksRejected(ProbeType);
        GoldpathCampaignMetrics.WindowClosed(ProbeType);

        // Exactly the contract: nothing exported that RFC §7 does not list, nothing listed that is not exported.
        Assert.Equal(Contract.Keys.Order(StringComparer.Ordinal), probe.Units.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, (unit, _)) in Contract)
        {
            Assert.True(probe.Units[name] == unit, $"{name}: unit '{probe.Units[name]}', expected '{unit}'");
        }

        var id = campaign.Id.ToString("N");
        foreach (var (name, (_, tags)) in Contract)
        {
            var mine = probe.Seen(name, m => tags.Length == 0
                || Equals(m.Tag("campaign"), id) || Equals(m.Tag("type"), ProbeType));
            Assert.True(mine.Count > 0, $"{name}: no measurement from this test");
            foreach (var measurement in mine)
            {
                Assert.Equal(tags.Order(StringComparer.Ordinal), measurement.Tags.Keys.Order(StringComparer.Ordinal));
            }
        }

        // The values say what the names promise.
        Assert.Equal(1_000, probe.Last("goldpath_campaign_tps_ceiling", id));
        Assert.Equal(50, probe.Last("goldpath_campaign_daily_quota", id));
        Assert.Equal(4, probe.Last("goldpath_campaign_released_today", id));
        Assert.Contains(probe.Seen("goldpath_campaign_type_max_tps", m => Equals(m.Tag("type"), ProbeType)), m => m.Value == 777);
        Assert.Contains(probe.Seen("goldpath_campaign_global_tps", _ => true), m => m.Value == ProbeGlobalTps);
        Assert.Contains(probe.Seen("goldpath_campaign_leader_last_tick", _ => true),
            m => m.Value >= startedAt.ToUnixTimeMilliseconds() / 1000d - 1);
        Assert.Equal("none", Assert.Single(probe.Seen("goldpath_campaign_binding_constraint", m => Equals(m.Tag("campaign"), id))
            .Select(m => m.Tag("constraint")).Distinct()));
        Assert.Equal(3, probe.Sum("goldpath_campaign_outcomes_total",
            m => Equals(m.Tag("type"), ProbeType) && Equals(m.Tag("outcome"), "succeeded") && Equals(m.Tag("error_code"), "none")));
        Assert.Equal(1, probe.Sum("goldpath_campaign_outcomes_total",
            m => Equals(m.Tag("type"), ProbeType) && Equals(m.Tag("outcome"), "retry") && Equals(m.Tag("error_code"), "HANDLER_EXCEPTION")));
        Assert.Equal(4, probe.Seen("goldpath_campaign_handler_duration", m => Equals(m.Tag("type"), ProbeType)).Count);
        Assert.Equal(4, probe.Seen("goldpath_campaign_sink_flush_lag", m => Equals(m.Tag("type"), ProbeType)).Count);
        Assert.All(probe.Seen("goldpath_campaign_sink_flush_lag", m => Equals(m.Tag("type"), ProbeType)), m => Assert.True(m.Value >= 0));
    }

    /// <summary>The sink counts what it MOVED: a duplicated outcome, or one for an attempt already settled, adds nothing.</summary>
    [Fact]
    public async Task TheOutcomesCounter_CountsRowsMoved_NotMessagesReceived()
    {
        using var probe = new MetricsProbe();
        const string type = "metricsdupes";
        using var fixture = new CampaignFixture(o =>
        {
            Fast(o);
            o.AddCampaign<TestTarget>(type, c => c.MaxTargets(100).Targets((_, _) => Targets(2)));
        });
        var campaign = await CreateAsync(fixture, new GoldpathCampaignPolicy(1_000, null, 100, null, null, "UTC"), type);
        await fixture.RunPacerSliceAsync();

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 1, CancellationToken.None));
        var success = new GoldpathCampaignOutcomeMessage(campaign.Id, 1, true, null) { Attempt = 1 };
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [success, success], CancellationToken.None);
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [success], CancellationToken.None);

        // A terminal refusal carries its code under outcome=failed.
        Assert.NotNull(await fixture.Engine.ClaimAsync(db, campaign.Id, 2, CancellationToken.None));
        var denied = new GoldpathCampaignOutcomeMessage(campaign.Id, 2, false, "denied") { Attempt = 1, Retryable = false, ErrorCode = "DENIED" };
        await fixture.Engine.ApplyOutcomesAsync(db, campaign.Id, [denied, denied], CancellationToken.None);

        Assert.Equal(1, probe.Sum("goldpath_campaign_outcomes_total",
            m => Equals(m.Tag("type"), type) && Equals(m.Tag("outcome"), "succeeded")));
        Assert.Equal(1, probe.Sum("goldpath_campaign_outcomes_total",
            m => Equals(m.Tag("type"), type) && Equals(m.Tag("outcome"), "failed") && Equals(m.Tag("error_code"), "DENIED")));
        Assert.Equal(2, probe.Sum("goldpath_campaign_outcomes_total", m => Equals(m.Tag("type"), type)));
        Assert.Equal(2, probe.Seen("goldpath_campaign_sink_flush_lag", m => Equals(m.Tag("type"), type)).Count);
    }

    /// <summary>An outcome naming a campaign that does not exist is a no-op, as before: nothing counted, nothing thrown.</summary>
    [Fact]
    public async Task AnOutcomeForNoCampaign_IsANoOp()
    {
        using var fixture = new CampaignFixture(Fast);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampaignTestContext>();
        var stray = Guid.NewGuid();
        await fixture.Engine.ApplyOutcomesAsync(db, stray,
            [new GoldpathCampaignOutcomeMessage(stray, 1, true, null), new GoldpathCampaignOutcomeMessage(stray, 2, false, "x")],
            CancellationToken.None);
    }

    // ---- the binding constraint, through the pacer ----

    [Fact]
    public async Task Binding_InFlight_WhenTheCapIsFull()
        => Assert.Equal("in_flight", await BindingAfterOneSliceAsync(new GoldpathCampaignPolicy(10_000, null, 2, null, null, "UTC")));

    [Fact]
    public async Task Binding_Quota_WhenTheDayIsSpent()
        => Assert.Equal("quota", await BindingAfterOneSliceAsync(new GoldpathCampaignPolicy(10_000, 3, 100, null, null, "UTC")));

    [Fact]
    public async Task Binding_Window_WhenTheWindowIsClosed()
    {
        var start = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
        var end = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        Assert.Equal("window", await BindingAfterOneSliceAsync(new GoldpathCampaignPolicy(10_000, null, 100, start, end, "UTC")));
    }

    [Fact]
    public async Task Binding_Tps_WhenTheCampaignsOwnRateHoldsIt()
        => Assert.Equal("tps", await BindingAfterOneSliceAsync(new GoldpathCampaignPolicy(2, null, 100, null, null, "UTC"), sourceSize: 200));

    [Fact]
    public async Task Binding_Type_WhenTheTargetSystemsCeilingHoldsIt()
        => Assert.Equal("type", await BindingAfterOneSliceAsync(
            new GoldpathCampaignPolicy(10_000, null, 10_000, null, null, "UTC"), sourceSize: 2_000, typeMaxTps: 100));

    [Fact]
    public async Task Binding_Global_WhenTheOneCeilingOverEveryoneHoldsIt()
        => Assert.Equal("global", await BindingAfterOneSliceAsync(
            new GoldpathCampaignPolicy(10_000, null, 10_000, null, null, "UTC"), sourceSize: 2_000, globalTps: 100));

    [Fact]
    public async Task Binding_None_WhenNothingIsLeftToRelease()
        => Assert.Equal("none", await BindingAfterOneSliceAsync(new GoldpathCampaignPolicy(10_000, null, 100, null, null, "UTC")));

    /// <summary>A campaign the pacer no longer runs leaves the per-campaign gauges with the next tick.</summary>
    [Fact]
    public async Task APausedCampaign_LeavesThePerCampaignGauges()
    {
        using var probe = new MetricsProbe();
        using var fixture = new CampaignFixture(Fast, sourceSize: 10);
        var campaign = await fixture.CreateAsync(new GoldpathCampaignPolicy(10_000, null, 2, null, null, "UTC"));
        await fixture.RunPacerSliceAsync();
        var id = campaign.Id.ToString("N");
        probe.Listener.RecordObservableInstruments();
        Assert.NotEmpty(probe.Seen("goldpath_campaign_binding_constraint", m => Equals(m.Tag("campaign"), id)));

        Assert.True((await fixture.Admin().PauseAsync(campaign.Id, "tester", CancellationToken.None)).Ok);
        await fixture.RunPacerSliceAsync();
        probe.Clear();
        probe.Listener.RecordObservableInstruments();
        Assert.Empty(probe.Seen("goldpath_campaign_binding_constraint", m => Equals(m.Tag("campaign"), id)));
        Assert.Empty(probe.Seen("goldpath_campaign_tps_ceiling", m => Equals(m.Tag("campaign"), id)));
    }

    // ---- the pure rules ----

    [Theory]
    [InlineData(10.0, 5L, null, "in_flight")]
    [InlineData(2.5, 5L, null, "tps")]
    [InlineData(10.0, 5L, 3L, "quota")]
    [InlineData(10.0, 5L, 5L, "quota")]       // a tie names the quota: it will not lift by itself next tick
    [InlineData(5.0, 5L, null, "in_flight")]  // a tie between the cap and the rate names the cap
    [InlineData(0.5, 0L, null, "in_flight")]
    [InlineData(10.0, -3L, 2L, "in_flight")]  // a cap overshot by a replay reads as full, not as negative room
    [InlineData(10.0, 5L, -1L, "quota")]
    public void OwnLimit_NamesTheSmallestRoom(double tps, long inFlight, long? quota, string expected)
        => Assert.Equal(expected, GoldpathCampaignPacerJob<CampaignTestContext>.OwnLimit(tps, inFlight, quota));

    [Theory]
    [InlineData("tps", 10, 10, 10, 4, "none")]       // granted 10, released 4: nothing more was ready
    [InlineData("tps", 10, 10, 10, 10, "tps")]
    [InlineData("in_flight", 0, 0, 0, 0, "in_flight")]
    [InlineData("tps", 10, 6, 6, 6, "type")]
    [InlineData("tps", 10, 6, 3, 3, "global")]
    [InlineData("tps", 10, 10, 3, 3, "global")]
    public void BindingConstraint_NamesWhatCutTheTick(string own, int demand, int afterType, int grant, int spent, string expected)
        => Assert.Equal(expected, GoldpathCampaignPacerJob<CampaignTestContext>.BindingConstraint(own, demand, afterType, grant, spent));

    [Fact]
    public void ErrorCodes_AreBoundedPerType_WithAnOtherOverflow()
    {
        var type = "codes-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < GoldpathCampaignMetrics.MaxErrorCodesPerType; i++)
        {
            Assert.Equal($"E{i}", GoldpathCampaignMetrics.ErrorCodeTag(type, $"E{i}"));
        }

        Assert.Equal(GoldpathCampaignMetrics.OtherErrorCode, GoldpathCampaignMetrics.ErrorCodeTag(type, "ONE_TOO_MANY"));
        Assert.Equal("E0", GoldpathCampaignMetrics.ErrorCodeTag(type, "E0"));   // a known code stays itself past the cap
        Assert.Equal("ONE_TOO_MANY", GoldpathCampaignMetrics.ErrorCodeTag("codes-" + Guid.NewGuid().ToString("N"), "ONE_TOO_MANY"));
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("İPTAL_ı", "İPTAL_ı")]   // a code is reported verbatim, culture plays no part
    [InlineData("SMS-4.2", "SMS-4.2")]
    [InlineData("user@example.test", "other")]   // free text never reaches a label: an address,
    [InlineData("HTTP 503", "other")]            // a phrase,
    [InlineData("a=b;c", "other")]               // a pair
    [InlineData("TIMEOUT\n", "other")]
    public void ErrorCodes_WithoutAValue_ReadNone_AndFreeText_ReadsOther(string? code, string expected)
        => Assert.Equal(expected, GoldpathCampaignMetrics.ErrorCodeTag("codes-" + Guid.NewGuid().ToString("N"), code));

    [Fact]
    public void ErrorCodes_LongerThanTheCap_ReadOther_WithoutTakingASlot()
    {
        var type = "codes-" + Guid.NewGuid().ToString("N");
        var exact = new string('X', GoldpathCampaignMetrics.MaxErrorCodeLength);
        Assert.Equal(exact, GoldpathCampaignMetrics.ErrorCodeTag(type, exact));
        Assert.Equal(GoldpathCampaignMetrics.OtherErrorCode, GoldpathCampaignMetrics.ErrorCodeTag(type, exact + "X"));

        // The over-long code took no slot: with `exact` in one, 31 more codes are still themselves.
        for (var i = 1; i < GoldpathCampaignMetrics.MaxErrorCodesPerType; i++)
        {
            Assert.Equal($"E{i}", GoldpathCampaignMetrics.ErrorCodeTag(type, $"E{i}"));
        }

        Assert.Equal(GoldpathCampaignMetrics.OtherErrorCode, GoldpathCampaignMetrics.ErrorCodeTag(type, "E99"));
    }

    // ---- helpers ----

    private static void Fast(GoldpathCampaignOptions o)
    {
        o.LeadershipSlice = TimeSpan.FromMilliseconds(400);
        o.LeaderTick = TimeSpan.FromMilliseconds(20);
        o.EnumerationBatchSize = 100;
    }

    private static async IAsyncEnumerable<TestTarget> Targets(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new TestTarget(i, $"m{i}@example.test");
        }

        await Task.CompletedTask;
    }

    private static async Task<GoldpathCampaign> CreateAsync(CampaignFixture fixture, GoldpathCampaignPolicy policy, string type = ProbeType)
    {
        using var scope = fixture.Services.CreateScope();
        return await fixture.Engine.CreateAsync(scope.ServiceProvider, type, "metrics run",
            new Dictionary<string, string>(), policy, tenant: null, actor: "tester", CancellationToken.None);
    }

    /// <summary>Every published item through <see cref="GoldpathCampaignItemHandler{TContext}"/>, then every outcome through the sink.</summary>
    private static async Task RunHandlerAndSinkAsync(CampaignFixture fixture)
    {
        var context = new IntegrationEventContext(null, null, null, 0, new Dictionary<string, object?>());
        foreach (var message in fixture.Publisher.Published.OfType<GoldpathCampaignItemMessage>().ToList())
        {
            using var scope = fixture.Services.CreateScope();
            var handler = new GoldpathCampaignItemHandler<CampaignTestContext>(fixture.Engine,
                scope.ServiceProvider.GetRequiredService<CampaignTestContext>(), scope.ServiceProvider, fixture.Publisher,
                NullLogger<GoldpathCampaignItemHandler<CampaignTestContext>>.Instance);
            await handler.HandleAsync(message, context, CancellationToken.None);
        }

        using var sinkScope = fixture.Services.CreateScope();
        var sink = new GoldpathCampaignOutcomeHandler<CampaignTestContext>(fixture.Engine,
            sinkScope.ServiceProvider.GetRequiredService<CampaignTestContext>());
        await sink.HandleAsync([.. fixture.Publisher.Published.OfType<GoldpathCampaignOutcomeMessage>()
            .Select(o => new IntegrationEvent<GoldpathCampaignOutcomeMessage>(o, context))], CancellationToken.None);
    }

    private static async Task<string?> BindingAfterOneSliceAsync(
        GoldpathCampaignPolicy policy, int sourceSize = 10, int? typeMaxTps = null, int? globalTps = null)
    {
        using var probe = new MetricsProbe();
        var type = "binding-" + Guid.NewGuid().ToString("N")[..12];
        using var fixture = new CampaignFixture(o =>
        {
            Fast(o);
            o.GlobalTps = globalTps;
            o.AddCampaign<TestTarget>(type, c =>
            {
                c.MaxTargets(10_000).Targets((_, _) => Targets(sourceSize));
                if (typeMaxTps is { } maxTps)
                {
                    c.MaxTps(maxTps);
                }
            });
        });
        var campaign = await CreateAsync(fixture, policy, type);
        // Enumerated BEFORE the slice (as R2PacingTests do): the binding constraint is about release.
        await fixture.EnumerateAllAsync(campaign);
        await fixture.RunPacerSliceAsync();
        probe.Listener.RecordObservableInstruments();
        var id = campaign.Id.ToString("N");
        return Assert.Single(probe.Seen("goldpath_campaign_binding_constraint", m => Equals(m.Tag("campaign"), id)))
            .Tag("constraint") as string;
    }

    /// <summary>One measurement: its value and its tags.</summary>
    private sealed record Seen(double Value, IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key) => Tags.GetValueOrDefault(key);
    }

    /// <summary>Listens to every <c>Goldpath.Campaign</c> instrument and keeps what it saw.</summary>
    private sealed class MetricsProbe : IDisposable
    {
        private readonly ConcurrentQueue<(string Name, Seen Measurement)> _seen = new();

        public MetricsProbe()
        {
            Listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Goldpath.Campaign")
                {
                    Units[instrument.Name] = instrument.Unit;
                    l.EnableMeasurementEvents(instrument);
                }
            };
            Listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            Listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            Listener.Start();
        }

        public MeterListener Listener { get; } = new();

        public ConcurrentDictionary<string, string?> Units { get; } = new(StringComparer.Ordinal);

        public List<Seen> Seen(string name, Func<Seen, bool> mine)
            => [.. _seen.Where(s => s.Name == name).Select(s => s.Measurement).Where(mine)];

        public double Last(string name, string campaign)
            => Seen(name, m => Equals(m.Tag("campaign"), campaign)).Last().Value;

        public double Sum(string name, Func<Seen, bool> mine) => Seen(name, mine).Sum(m => m.Value);

        public void Clear() => _seen.Clear();

        public void Dispose() => Listener.Dispose();

        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            _seen.Enqueue((instrument.Name, new Seen(value, copy)));
        }
    }
}
