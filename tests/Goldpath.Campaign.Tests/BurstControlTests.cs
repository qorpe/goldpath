using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// #292 (S-FLEET-03 strict; campaign RFC R1 test plan item 2, R2.4): after an idle stretch no one-second window, at
/// tick granularity, releases more than the ceiling — for a campaign's <c>Tps</c>, a type's <c>MaxTps</c> and the
/// <c>GlobalTps</c> — and the steady release rate is what it was.
/// </summary>
public class BurstControlTests
{
    private const int Ceiling = 100;
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);
    private const int TicksPerSecond = 20;
    private const int IdleTicks = 30;     // 1.5 s of banking: before #292 the bucket held a whole second of the ceiling
    private const int ReleaseTicks = 40;  // 2 s of hungry release after the idle stretch

    public enum Bucket
    {
        CampaignTps,
        TypeMaxTps,
        GlobalTps,
    }

    // ---- the bucket's capacity (pure) ----

    [Theory]
    [InlineData(100, 50, 5)]      // 5 a tick: exactly one tick's worth
    [InlineData(1_000, 250, 250)] // the default tick: exactly one tick's worth
    [InlineData(25, 100, 3)]      // 2.5 a tick: plus room for the carried half
    [InlineData(7, 250, 2.5)]     // 1.75 a tick: plus room for a carried three quarters
    [InlineData(3, 100, 1.2)]     // 0.3 a tick: plus room for a carried 0.9
    [InlineData(50, 140, 7)]      // 50 × 0.14 s is 7.000000000000001 in floating point; exact integers keep it at 7
    [InlineData(10, 2_000, 10)]   // a tick longer than a second never holds more than the ceiling
    [InlineData(10, 1_000, 10)]
    [InlineData(0, 50, 0)]
    public void TheCapacityIsOneTicksWorth_PlusTheLargestCarryARefillLeaves(int ceiling, int tickMs, double expected)
        => Assert.Equal(expected, GoldpathCampaignPacerJob<CampaignTestContext>.BucketCapacity(ceiling, TimeSpan.FromMilliseconds(tickMs)), 9);

    [Theory]
    [InlineData(100, 50)]
    [InlineData(25, 100)]
    [InlineData(3, 100)]
    [InlineData(50, 140)]
    [InlineData(1_000, 250)]
    [InlineData(7, 250)]         // 1.75 a tick: the carry reaches 0.75, which a capacity of 2 would clip
    [InlineData(33, 20)]         // 0.66 a tick
    public void AfterAnIdleStretch_NoSecondOfTicksReleasesMoreThanTheCeiling_AndTheSteadyRateHolds(int ceiling, int tickMs)
    {
        // The pacer's own arithmetic: refill, spend what is whole, carry the rest.
        var tick = TimeSpan.FromMilliseconds(tickMs);
        var banked = 0d;
        for (var i = 0; i < 200; i++)
        {
            banked = GoldpathCampaignPacerJob<CampaignTestContext>.Refill(banked, ceiling, tick);   // idle: nothing spent
        }

        var released = new List<int>();
        for (var i = 0; i < 400; i++)
        {
            banked = GoldpathCampaignPacerJob<CampaignTestContext>.Refill(banked, ceiling, tick);
            var spent = (int)Math.Floor(banked);
            banked -= spent;
            released.Add(spent);
        }

        var perSecond = (int)(TimeSpan.FromSeconds(1).Ticks / tick.Ticks);
        Assert.InRange(MaxWindow(released, Math.Max(1, perSecond)), 0, ceiling);
        // The steady rate is the ceiling: over 400 ticks the carry loses less than one unit.
        Assert.InRange(released.Sum(), (ceiling * 400 * tick.TotalSeconds) - 1, ceiling * 400 * tick.TotalSeconds);
    }

    // ---- the pacer, one bucket binding at a time ----

    [Theory]
    [InlineData(Bucket.CampaignTps)]
    [InlineData(Bucket.TypeMaxTps)]
    [InlineData(Bucket.GlobalTps)]
    public async Task AfterAnIdleStretch_NoSecondOfTicksReleasesMoreThanTheCeiling(Bucket bucket)
    {
        using var fixture = new CampaignFixture(o =>
        {
            o.LeadershipSlice = TimeSpan.FromMinutes(1);   // the test ends the slice itself, after ReleaseTicks
            o.LeaderTick = Tick;
            o.EnumerationBatchSize = 500;
            if (bucket == Bucket.GlobalTps)
            {
                o.GlobalTps = Ceiling;
            }

            o.AddCampaign<TestTarget>("bulk", c =>
            {
                c.MaxTargets(10_000).Targets((_, _) => Targets(5_000));
                if (bucket == Bucket.TypeMaxTps)
                {
                    c.MaxTps(Ceiling);
                }
            });
        });

        // Only the bucket under test binds: the campaign's own Tps is generous unless it IS the bucket. MaxInFlight 1
        // is the idle stretch — one release, then nothing while every bucket keeps refilling.
        var policy = new GoldpathCampaignPolicy(bucket == Bucket.CampaignTps ? Ceiling : 100_000, null, 1, null, null, "UTC");
        GoldpathCampaign campaign;
        using (var scope = fixture.Services.CreateScope())
        {
            campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, "bulk", "burst",
                new Dictionary<string, string>(), policy, tenant: null, actor: "tester", CancellationToken.None);
        }

        await fixture.EnumerateAllAsync(campaign);

        var released = await RunWithReliefAsync(fixture, [campaign.Id]);

        var afterIdle = released.Skip(IdleTicks).ToList();   // tick IdleTicks + 1 onward: the ticks after the relief
        Assert.Equal(ReleaseTicks, afterIdle.Count);
        Assert.True(afterIdle.Sum() > Ceiling, $"the release phase must run longer than one second to prove anything (released {afterIdle.Sum()})");
        var worst = MaxWindow(afterIdle, TicksPerSecond);
        Assert.True(worst <= Ceiling, $"{bucket}: one second of ticks released {worst} > ceiling {Ceiling}; per tick: {string.Join(",", afterIdle.Take(25))}");
        // The steady rate holds: from the second second on, every tick releases its full refill.
        Assert.All(afterIdle.Skip(TicksPerSecond), perTick => Assert.Equal(Ceiling / TicksPerSecond, perTick));
    }

    [Fact]
    public async Task ScenarioFleet03_TwoCampaignsAt800_UnderGlobalTps1000_NeverReleaseAbove1000InAnySecondOfTicks()
    {
        // S-FLEET-03 at its own numbers: GlobalTps 1000, two campaigns at tps 800 each, both hungry after an idle stretch.
        const int globalTps = 1_000;
        using var fixture = new CampaignFixture(o =>
        {
            o.LeadershipSlice = TimeSpan.FromMinutes(1);
            o.LeaderTick = Tick;
            o.EnumerationBatchSize = 5_000;
            o.GlobalTps = globalTps;
            o.AddCampaign<TestTarget>("bulk", c => c.MaxTargets(10_000).Targets((_, _) => Targets(5_000)));
        });

        var ids = new List<Guid>();
        foreach (var name in new[] { "first", "second" })
        {
            GoldpathCampaign campaign;
            using (var scope = fixture.Services.CreateScope())
            {
                campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, "bulk", name, new Dictionary<string, string>(),
                    new GoldpathCampaignPolicy(800, null, 1, null, null, "UTC"), tenant: null, actor: "tester", CancellationToken.None);
            }

            await fixture.EnumerateAllAsync(campaign);
            ids.Add(campaign.Id);
        }

        var afterIdle = (await RunWithReliefAsync(fixture, ids)).Skip(IdleTicks).ToList();

        Assert.Equal(ReleaseTicks, afterIdle.Count);
        var worst = MaxWindow(afterIdle, TicksPerSecond);
        Assert.True(worst <= globalTps, $"one second of ticks released {worst} > {globalTps}; per tick: {string.Join(",", afterIdle.Take(25))}");
        // Both hungry above the global ceiling: from the second second on, every tick releases the global refill.
        Assert.All(afterIdle.Skip(TicksPerSecond), perTick => Assert.Equal(globalTps / TicksPerSecond, perTick));
        Assert.All(ids, id => Assert.True(fixture.Reload(id).ReleasedThrough > globalTps / 2, "both campaigns must have released"));
    }

    /// <summary>
    /// Runs one pacer slice: <see cref="IdleTicks"/> ticks at <c>MaxInFlight</c> 1 (one release each, then nothing while
    /// every bucket refills), the cap lifted between two ticks, then <see cref="ReleaseTicks"/> ticks of hungry release.
    /// Returns what each tick released.
    /// </summary>
    private static async Task<List<int>> RunWithReliefAsync(CampaignFixture fixture, IReadOnlyList<Guid> campaigns)
    {
        using var stop = new CancellationTokenSource();
        using var scope = fixture.Services.CreateScope();
        var ticks = new TickRecorder(scope.ServiceProvider, () => fixture.Publisher.Published.Count, tick =>
        {
            if (tick == IdleTicks + 1)
            {
                // The relief, BETWEEN two ticks on the pacer's own thread: the slice shares one SQLite connection.
                foreach (var id in campaigns)
                {
                    var lifted = fixture.Admin().ThrottleAsync(id, new GoldpathCampaignThrottle(MaxInFlight: 100_000), "tester", CancellationToken.None)
                        .GetAwaiter().GetResult();
                    Assert.True(lifted.Ok);
                }
            }

            if (tick == IdleTicks + ReleaseTicks + 1)
            {
                stop.Cancel();
            }
        });
        var job = fixture.Pacer();
        var context = CampaignFixture.CreateContext(ticks);
        var plan = await job.PlanAsync(context, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => job.ExecuteChunkAsync(CampaignFixture.MakeChunk(0, plan.ChunkPayloads[0]), context, stop.Token));
        return ticks.ReleasedPerTick();
    }

    private static int MaxWindow(IReadOnlyList<int> perTick, int window)
    {
        var worst = 0;
        for (var i = 0; i < perTick.Count; i++)
        {
            worst = Math.Max(worst, perTick.Skip(i).Take(window).Sum());
        }

        return worst;
    }

    private static async IAsyncEnumerable<TestTarget> Targets(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return new TestTarget(i, $"bulk{i}@example.test");
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// The slice's service provider, seen from the pacer's tick boundary: the pacer opens exactly one scope from it at
    /// the start of every tick, so each scope created is one tick begun, and what was published by then closes the tick
    /// before it. Exact whatever the machine's load: nothing here is inferred from the clock.
    /// </summary>
    private sealed class TickRecorder(IServiceProvider inner, Func<int> published, Action<int> beforeTick) : IServiceProvider, IServiceScopeFactory
    {
        private readonly List<int> _publishedAtStart = [];

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope()
        {
            _publishedAtStart.Add(published());
            beforeTick(_publishedAtStart.Count);
            return inner.GetRequiredService<IServiceScopeFactory>().CreateScope();
        }

        /// <summary>What each finished tick released, in tick order.</summary>
        public List<int> ReleasedPerTick()
            => [.. _publishedAtStart.Zip(_publishedAtStart.Skip(1), (start, next) => next - start)];
    }
}
