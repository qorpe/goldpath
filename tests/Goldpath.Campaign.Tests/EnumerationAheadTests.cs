using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// A clock that moves only when told to, or when the pacer sleeps: a timer advances it by its
/// due time and fires at once. A leadership slice then takes an exact number of ticks however
/// fast the machine is, and work costs time only where a test charges it.
/// </summary>
internal sealed class TickClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
        }

        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new FiredTimer();
    }

    private sealed class FiredTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// #288: the snapshot is no longer bound to the leader tick. Phase 1 of a tick materializes one
/// batch per campaign; what is left of the tick enumerates further, after the releases.
/// </summary>
public class EnumerationAheadTests
{
    private const int FleetSize = 100_000;
    private const int SliceTicks = 10;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

    /// <summary>The fleet type's selector; <paramref name="onYield"/> sees each target before it is handed over.</summary>
    private static async IAsyncEnumerable<TestTarget> Fleet(int count, Action<int>? onYield = null)
    {
        for (var i = 1; i <= count; i++)
        {
            onYield?.Invoke(i);
            yield return new TestTarget(i, $"device{i}@example.test");
        }

        await Task.CompletedTask;
    }

    private static CampaignFixture FleetFixture(Action<int>? onYield = null, int maxTargets = FleetSize * 2)
        => new(o =>
        {
            o.LeaderTick = Tick;
            o.LeadershipSlice = Tick * SliceTicks;
            o.AddCampaign<TestTarget>("fleet", c => c
                .MaxTargets(maxTargets)
                .Targets((_, _) => Fleet(FleetSize, onYield)));
        }, sourceSize: 500);

    private static async Task<GoldpathCampaign> CreateFleetAsync(CampaignFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        return await fixture.Engine.CreateAsync(scope.ServiceProvider, "fleet", "snapshot",
            new Dictionary<string, string>(), new GoldpathCampaignPolicy(10_000, null, 1_000_000, null, null, "UTC"),
            tenant: null, actor: "tester", CancellationToken.None);
    }

    /// <summary>A running campaign beside the snapshot: 40 TPS is ten releases a tick, 500 targets outlast the slice.</summary>
    private static async Task<GoldpathCampaign> CreateRunningAsync(CampaignFixture fixture)
    {
        var running = await fixture.CreateAsync(new GoldpathCampaignPolicy(40, null, 1_000_000, null, null, "UTC"));
        await fixture.EnumerateAllAsync(running);
        Assert.Equal(GoldpathCampaignState.Running, fixture.Reload(running.Id).State);
        return running;
    }

    /// <summary>Publishes of one campaign, stamped with the tick they happened in (1-based).</summary>
    private sealed class TickStampingPublisher(
        RecordingPublisher inner, TickClock clock, DateTimeOffset sliceStart, Guid campaignId, Action<int>? onRelease)
        : MassTransit.IPublishEndpoint
    {
        public List<int> Ticks { get; } = [];

        public Task Publish<T>(T message, CancellationToken cancellationToken = default)
            where T : class
        {
            if (message is GoldpathCampaignItemMessage item && item.CampaignId == campaignId)
            {
                var tick = (int)((clock.GetUtcNow() - sliceStart).Ticks / Tick.Ticks) + 1;
                Ticks.Add(tick);
                onRelease?.Invoke(tick);
            }

            return inner.Publish(message, cancellationToken);
        }

        public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public Task Publish<T>(T message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public Task Publish(object message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish(object message, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish(object message, Type messageType, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task Publish<T>(object values, CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext<T>> publishPipe, CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public Task Publish<T>(object values, MassTransit.IPipe<MassTransit.PublishContext> publishPipe, CancellationToken cancellationToken = default)
            where T : class => throw new NotSupportedException();

        public MassTransit.ConnectHandle ConnectPublishObserver(MassTransit.IPublishObserver observer) => throw new NotSupportedException();
    }

    /// <summary>Runs one slice on <paramref name="clock"/>, recording which ticks released <paramref name="watched"/>.</summary>
    private static async Task<List<int>> RunSliceAsync(CampaignFixture fixture, TickClock clock, Guid watched, Action<int>? onRelease = null)
    {
        var publisher = new TickStampingPublisher(fixture.Publisher, clock, clock.GetUtcNow(), watched, onRelease);
        using var scope = fixture.Services.CreateScope();
        using var overlay = new OverlayProvider(scope.ServiceProvider, publisher);
        var pacer = new GoldpathCampaignPacerJob<CampaignTestContext>(
            fixture.Engine, fixture.Options, clock, NullLogger<GoldpathCampaignPacerJob<CampaignTestContext>>.Instance);
        await pacer.ExecuteChunkAsync(CampaignFixture.MakeChunk(0, "lead"), CampaignFixture.CreateContext(overlay), CancellationToken.None);
        return publisher.Ticks;
    }

    /// <summary>The fixture's services, with the publisher swapped for the tick-stamping one in every scope.</summary>
    private sealed class OverlayProvider(IServiceProvider inner, MassTransit.IPublishEndpoint publisher)
        : IServiceProvider, IServiceScopeFactory, IDisposable
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(MassTransit.IPublishEndpoint) ? publisher
                : serviceType == typeof(IServiceScopeFactory) ? this
                : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new OverlayScope(inner.CreateScope(), publisher);

        public void Dispose()
        {
        }

        private sealed class OverlayScope(IServiceScope scope, MassTransit.IPublishEndpoint publisher) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new OverlayProvider(scope.ServiceProvider, publisher);

            public void Dispose() => scope.Dispose();
        }
    }

    [Fact]
    public async Task ASnapshotFinishesInFewerTicksThanItHasBatches_WhileARunningCampaignReleasesEveryTick()
    {
        using var fixture = FleetFixture();
        var running = await CreateRunningAsync(fixture);
        var fleet = await CreateFleetAsync(fixture);
        var clock = new TickClock(DateTimeOffset.UtcNow);

        var releaseTicks = await RunSliceAsync(fixture, clock, running.Id);

        // One batch per tick would need ceil(100k / 2,000) = 50 ticks; the slice has ten.
        var batches = (int)Math.Ceiling(FleetSize / (double)fixture.Options.EnumerationBatchSize);
        Assert.True(SliceTicks < batches);
        var after = fixture.Reload(fleet.Id);
        Assert.True(after.EnumerationComplete, $"enumerated {after.EnumeratedThrough} of {FleetSize} in {SliceTicks} ticks");
        Assert.Equal(FleetSize, after.EnumeratedThrough);
        Assert.Equal(GoldpathCampaignState.Running, after.State);
        Assert.Equal(FleetSize, fixture.Query(db => db.Set<GoldpathCampaignItem>().Count(i => i.CampaignId == fleet.Id)));

        // ...and the campaign that was already running got its releases in every one of them.
        Assert.Equal(Enumerable.Range(1, SliceTicks), releaseTicks.Distinct().Order());
    }

    [Fact]
    public async Task EnumerationStopsWhereTheTickEnds_SoTheNextTicksReleasesAreOnTime()
    {
        // Each target costs 50 µs on the pacer's clock: a 2,000 batch is 100 ms of a 250 ms
        // tick. Phase 1 takes one batch, Phase 5 one more (200 ms + the 100 ms estimate would
        // pass the tick's end), then the pacer sleeps the rest.
        TickClock? clock = null;
        using var fixture = FleetFixture(_ => clock?.Advance(TimeSpan.FromTicks(500)));
        var running = await CreateRunningAsync(fixture);
        var fleet = await CreateFleetAsync(fixture);
        clock = new TickClock(DateTimeOffset.UtcNow);

        var releaseTicks = await RunSliceAsync(fixture, clock, running.Id);

        Assert.Equal(Enumerable.Range(1, SliceTicks), releaseTicks.Distinct().Order());
        Assert.Equal(2L * fixture.Options.EnumerationBatchSize * SliceTicks, fixture.Reload(fleet.Id).EnumeratedThrough);
    }

    [Fact]
    public async Task ACampaignAllowedNothingThisTick_StillCountsAsReleasing()
    {
        // At 1 TPS a 250 ms tick banks a quarter unit: the running campaign's allowance is 0 in
        // most ticks and 1 in every fourth. It is still releasing, so every tick keeps the
        // estimate guard (2 steps, never the third that would push the next tick late).
        TickClock? clock = null;
        using var fixture = FleetFixture(_ => clock?.Advance(TimeSpan.FromTicks(500)));
        var running = await fixture.CreateAsync(new GoldpathCampaignPolicy(1, null, 1_000_000, null, null, "UTC"));
        await fixture.EnumerateAllAsync(running);
        var fleet = await CreateFleetAsync(fixture);
        clock = new TickClock(DateTimeOffset.UtcNow);

        var releaseTicks = await RunSliceAsync(fixture, clock, running.Id);

        Assert.NotEmpty(releaseTicks);
        Assert.Equal(2L * fixture.Options.EnumerationBatchSize * SliceTicks, fixture.Reload(fleet.Id).EnumeratedThrough);
    }

    [Fact]
    public async Task WithNothingReleasing_EnumerationTakesTheWholeTick_AndOverrunsByAtMostOneStep()
    {
        // The same 100 ms step, no other campaign: steps start at 0, 100 and 200 ms (200 is
        // still inside the 250 ms tick), the tick ends at 300 ms and the pacer does not sleep.
        // The 2,500 ms slice then starts ticks at 0, 300, ..., 2,400: nine ticks, three steps each.
        TickClock? clock = null;
        using var fixture = FleetFixture(_ => clock?.Advance(TimeSpan.FromTicks(500)));
        var fleet = await CreateFleetAsync(fixture);
        clock = new TickClock(DateTimeOffset.UtcNow);
        var sliceStart = clock.GetUtcNow();

        await RunSliceAsync(fixture, clock, Guid.Empty);

        Assert.Equal(9L * 3 * fixture.Options.EnumerationBatchSize, fixture.Reload(fleet.Id).EnumeratedThrough);
        Assert.Equal(TimeSpan.FromMilliseconds(2_700), clock.GetUtcNow() - sliceStart);   // the last tick overran by 50 ms, under one step
    }

    [Fact]
    public async Task ATargetCeilingBreachedAheadPausesTheCampaign_AndItStepsNoFurther()
    {
        // Phase 1 takes 1..2,000; the ceiling (7,000) is crossed in Phase 5 of the first tick.
        using var fixture = FleetFixture(maxTargets: 7_000);
        var fleet = await CreateFleetAsync(fixture);

        await RunSliceAsync(fixture, new TickClock(DateTimeOffset.UtcNow), Guid.Empty);

        var stopped = fixture.Reload(fleet.Id);
        Assert.Equal(GoldpathCampaignState.Paused, stopped.State);
        Assert.False(stopped.EnumerationComplete);   // a step on the paused row would read the stream dry and say "complete"
        Assert.Equal(7_000, stopped.EnumeratedThrough);
        Assert.Contains("target ceiling (7000) exceeded", stopped.LastVerb);
        Assert.Equal(7_000, fixture.Query(db => db.Set<GoldpathCampaignItem>().Count(i => i.CampaignId == fleet.Id)));
    }

    [Fact]
    public async Task APauseThatLandsMidTick_StopsTheSnapshot_AndAResumeInTheSameSliceLosesAndDoublesNothing()
    {
        CampaignFixture? fixture = null;
        Guid fleetId = default;
        var paused = false;
        var resumed = false;
        using var created = FleetFixture(i =>
        {
            // Target 5,001 is read in Phase 5 of the first tick (Phase 1 took 1..2,000):
            // the operator's pause lands between the tick's load and its next durable write.
            if (i == 5_001 && !paused && fixture is not null)
            {
                paused = true;
                Assert.True(fixture.Admin().PauseAsync(fleetId, "tester", CancellationToken.None).GetAwaiter().GetResult().Ok);
            }
        });
        fixture = created;
        var running = await CreateRunningAsync(fixture);
        var fleet = await CreateFleetAsync(fixture);
        fleetId = fleet.Id;

        // The running campaign's third tick resumes the snapshot inside the SAME slice: the
        // stream the race left ahead of the watermark must not be the one that carries on.
        Exception? stateAtResume = null;
        await RunSliceAsync(fixture, new TickClock(DateTimeOffset.UtcNow), running.Id, tick =>
        {
            if (tick == 3 && !resumed)
            {
                resumed = true;
                try
                {
                    var stopped = fixture.Reload(fleetId);
                    Assert.Equal(GoldpathCampaignState.Paused, stopped.State);
                    Assert.False(stopped.EnumerationComplete);
                    Assert.Equal(4_000, stopped.EnumeratedThrough);   // the batch the pause raced was never written
                    Assert.True(fixture.Admin().ResumeAsync(fleetId, "tester", CancellationToken.None).GetAwaiter().GetResult().Ok);
                }
                catch (Exception e)
                {
                    stateAtResume = e;
                }
            }
        });

        Assert.True(paused);
        Assert.True(resumed);
        Assert.Null(stateAtResume);
        var done = fixture.Reload(fleet.Id);
        Assert.True(done.EnumerationComplete);
        Assert.Equal(FleetSize, done.EnumeratedThrough);
        var items = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking()
            .Where(i => i.CampaignId == fleet.Id).Select(i => new { i.Seq, i.TargetJson }).ToList());
        Assert.Equal(FleetSize, items.Count);
        Assert.Equal(Enumerable.Range(1, FleetSize).Select(i => (long)i), items.Select(i => i.Seq).Order());
        Assert.Equal(FleetSize, items.Select(i => i.TargetJson).Distinct().Count());
        Assert.All(items, item => Assert.Contains($"\"Id\":{item.Seq},", item.TargetJson));   // Seq n carries target n: nothing shifted
    }
}
