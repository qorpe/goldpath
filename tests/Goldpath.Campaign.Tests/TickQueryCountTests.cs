using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// #285's measurement: how many database commands one pacer tick issues for one running, hungry, fully enumerated
/// campaign, in the steady state (every tick after the slice's first, whose start-of-slice bookkeeping is not per tick).
/// The pacer runs on <see cref="TickClock"/>, so each tick is exactly <see cref="Tick"/> of clock time and a command is
/// attributed to the tick it ran in. The count is PINNED: a change that adds or removes a per-tick query turns this red,
/// so it is made on purpose and the bench report's number moves with it (#285: "remove a query only if a test proves
/// the behaviour it served is kept").
/// </summary>
public class TickQueryCountTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private const int SliceTicks = 12;

    /// <summary>
    /// The steady-state commands per tick. Measured as 7 on 2026-11-10 (the same on main and on #285's branch), then 8
    /// once R3-S8 (#290, #529) landed beside it on 2026-11-11: the pacer reads the live ceilings (the global row and every
    /// declared target system's) in ONE query a tick, by design (campaign RFC R3.2), so a ceiling edited by an operator
    /// bites within one tick.
    /// </summary>
    private const int CommandsPerSteadyTick = 8;

    [Fact]
    public async Task One_hungry_campaign_costs_a_fixed_number_of_commands_per_tick()
    {
        var clock = new TickClock(DateTimeOffset.UtcNow);
        var commands = new ConcurrentQueue<long>();
        using var fixture = new CampaignFixture(
            o =>
            {
                o.LeadershipSlice = Tick * SliceTicks;
                o.LeaderTick = Tick;
                o.EnumerationBatchSize = 5_000;
            },
            sourceSize: 1_000,
            extraServices: services => services.ConfigureDbContext<CampaignTestContext>(
                o => o.AddInterceptors(new Stamp(clock, commands))));
        GoldpathCampaign campaign;
        using (var scope = fixture.Services.CreateScope())
        {
            campaign = await fixture.Engine.CreateAsync(scope.ServiceProvider, "winback", "measured", new Dictionary<string, string>(),
                new GoldpathCampaignPolicy(40, null, 100_000, null, null, "UTC"), tenant: null, actor: "tester", CancellationToken.None);
        }

        await fixture.EnumerateAllAsync(campaign);
        commands.Clear();
        var sliceStart = clock.GetUtcNow().UtcTicks;

        using (var scope = fixture.Services.CreateScope())
        {
            var pacer = new GoldpathCampaignPacerJob<CampaignTestContext>(
                fixture.Engine, fixture.Options, clock, NullLogger<GoldpathCampaignPacerJob<CampaignTestContext>>.Instance);
            await pacer.ExecuteChunkAsync(CampaignFixture.MakeChunk(0, "lead"), CampaignFixture.CreateContext(scope.ServiceProvider), CancellationToken.None);
        }

        var perTick = commands
            .Select(at => (int)((at - sliceStart) / Tick.Ticks))
            .GroupBy(tick => tick)
            .ToDictionary(g => g.Key, g => g.Count());
        var steady = Enumerable.Range(1, SliceTicks - 2).Select(t => perTick.GetValueOrDefault(t)).ToList();
        output.WriteLine($"TICK-QUERIES per tick 0..{SliceTicks - 1}: {string.Join(",", Enumerable.Range(0, SliceTicks).Select(t => perTick.GetValueOrDefault(t)))}");
        Assert.True(fixture.Reload(campaign.Id).ReleasedThrough > 0, "the campaign released nothing; the measurement would be of an idle tick");
        Assert.All(steady, count => Assert.Equal(CommandsPerSteadyTick, count));
    }

    /// <summary>Records the clock time of every command the context sends.</summary>
    private sealed class Stamp(TimeProvider clock, ConcurrentQueue<long> commands) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            commands.Enqueue(clock.GetUtcNow().UtcTicks);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            commands.Enqueue(clock.GetUtcNow().UtcTicks);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            commands.Enqueue(clock.GetUtcNow().UtcTicks);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
