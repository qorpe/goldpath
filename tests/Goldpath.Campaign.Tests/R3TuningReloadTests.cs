using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Goldpath.Campaign.Tests;

/// <summary>
/// Campaign RFC R3.3 (#317): <c>RetryBackoff</c>, <c>RetryJitter</c>, <c>OrphanReleaseAfter</c> and
/// <c>StaleClaimAfter</c> reload from <c>Goldpath:Campaign</c> through <c>IOptionsMonitor</c>, with no
/// restart. Every test builds its host through <c>AddGoldpathCampaign</c>, so what is proven is the
/// module's own wiring, not a hand-assembled monitor.
/// </summary>
public class R3TuningReloadTests
{
    private static readonly TimeSpan[] CodeLadder = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    [Fact]
    public void A_reload_changes_the_ladder_the_jitter_and_both_ages_with_no_restart()
    {
        using var rig = new Rig();
        var options = rig.Options;
        Assert.Equal(CodeLadder, options.RetryBackoff);
        Assert.Equal(TimeSpan.FromMinutes(5), options.OrphanReleaseAfter);
        Assert.Equal(TimeSpan.FromMinutes(10), options.StaleClaimAfter);

        rig.Config.Set(new()
        {
            ["Goldpath:Campaign:RetryBackoff:0"] = "00:01:00",
            ["Goldpath:Campaign:RetryBackoff:1"] = "00:07:00",
            ["Goldpath:Campaign:RetryJitter"] = "0",
            ["Goldpath:Campaign:OrphanReleaseAfter"] = "00:00:45",
            ["Goldpath:Campaign:StaleClaimAfter"] = "00:03:00",
        });

        Assert.Same(options, rig.Host.Services.GetRequiredService<GoldpathCampaignOptions>());
        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(7)], options.RetryBackoff);
        Assert.Equal(0, options.RetryJitter);
        Assert.Equal(TimeSpan.FromMinutes(1), options.NextRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(7), options.NextRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(7), options.NextRetryDelay(9));   // the last rung repeats
        Assert.Equal(TimeSpan.FromSeconds(45), options.OrphanReleaseAfter);
        Assert.Equal(TimeSpan.FromMinutes(3), options.StaleClaimAfter);

        // A key that leaves configuration falls back to the code value.
        rig.Config.Set(new() { ["Goldpath:Campaign:StaleClaimAfter"] = "00:03:00" });
        Assert.Equal(CodeLadder, options.RetryBackoff);
        Assert.Equal(0.2, options.RetryJitter);
        Assert.Equal(TimeSpan.FromMinutes(5), options.OrphanReleaseAfter);
        Assert.Equal(TimeSpan.FromMinutes(3), options.StaleClaimAfter);
    }

    [Fact]
    public void A_configured_ladder_replaces_the_code_ladder_and_never_appends_to_it()
    {
        var five = new[] { 1, 2, 3, 4, 5 }.Select(m => TimeSpan.FromMinutes(m)).ToArray();
        using var rig = new Rig(o => o.RetryBackoff = five, new()
        {
            ["Goldpath:Campaign:RetryBackoff:0"] = "00:00:10",
            ["Goldpath:Campaign:RetryBackoff:1"] = "00:00:20",
        });

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], rig.Options.RetryBackoff);

        // And a reload to one rung is one rung, not one plus the two before it.
        rig.Config.Set(new() { ["Goldpath:Campaign:RetryBackoff:0"] = "00:05:00" });
        Assert.Equal([TimeSpan.FromMinutes(5)], rig.Options.RetryBackoff);
    }

    [Fact]
    public void A_code_value_set_after_the_host_is_built_still_reads_while_configuration_leaves_its_key_unset()
    {
        using var rig = new Rig(configuration: new() { ["Goldpath:Campaign:OrphanReleaseAfter"] = "00:02:00" });

        rig.Options.RetryJitter = 0;
        rig.Options.OrphanReleaseAfter = TimeSpan.FromHours(1);

        Assert.Equal(0, rig.Options.RetryJitter);
        Assert.Equal(TimeSpan.FromMinutes(2), rig.Options.OrphanReleaseAfter);   // configuration wins
    }

    [Theory]
    [InlineData("RetryBackoff", "")]                     // how a JSON [] arrives: an empty ladder
    [InlineData("RetryBackoff", "00:01:00")]             // one value where a list belongs
    [InlineData("RetryBackoff:0", "-00:00:30")]          // a negative rung
    [InlineData("RetryBackoff:0", "half a minute")]      // not a TimeSpan
    [InlineData("RetryJitter", "1.5")]
    [InlineData("RetryJitter", "-0.1")]
    [InlineData("RetryJitter", "NaN")]
    [InlineData("RetryJitter", "0,5")]                   // a tr-TR decimal comma: configuration is invariant
    [InlineData("OrphanReleaseAfter", "00:00:00")]
    [InlineData("OrphanReleaseAfter", "-00:01:00")]
    [InlineData("StaleClaimAfter", "00:00:00")]
    [InlineData("StaleClaimAfter", "ten minutes")]
    public void An_invalid_reload_is_refused_whole_logged_with_its_key_and_counted(string key, string value)
    {
        using var rig = new Rig(configuration: new()
        {
            ["Goldpath:Campaign:RetryBackoff:0"] = "00:01:00",
            ["Goldpath:Campaign:RetryJitter"] = "0.1",
            ["Goldpath:Campaign:OrphanReleaseAfter"] = "00:00:45",
            ["Goldpath:Campaign:StaleClaimAfter"] = "00:03:00",
        });
        using var refusals = new RefusalCounter();

        // One invalid key beside valid changes to the other three: nothing of the reload applies.
        var reload = new Dictionary<string, string?>
        {
            ["Goldpath:Campaign:RetryBackoff:0"] = "00:09:00",
            ["Goldpath:Campaign:RetryJitter"] = "0.3",
            ["Goldpath:Campaign:OrphanReleaseAfter"] = "00:09:00",
            ["Goldpath:Campaign:StaleClaimAfter"] = "00:09:00",
        };
        if (key.StartsWith("RetryBackoff", StringComparison.Ordinal))
        {
            reload.Remove("Goldpath:Campaign:RetryBackoff:0");
        }

        reload[$"Goldpath:Campaign:{key}"] = value;
        rig.Config.Set(reload);

        Assert.Equal([TimeSpan.FromMinutes(1)], rig.Options.RetryBackoff);
        Assert.Equal(0.1, rig.Options.RetryJitter);
        Assert.Equal(TimeSpan.FromSeconds(45), rig.Options.OrphanReleaseAfter);
        Assert.Equal(TimeSpan.FromMinutes(3), rig.Options.StaleClaimAfter);
        Assert.Equal(1, refusals.Count);
        var warning = Assert.Single(rig.Logs.Warnings);
        Assert.Contains($"Goldpath:Campaign:{key.Split(':')[0]}", warning, StringComparison.Ordinal);
        Assert.Contains("previous values stay in force", warning, StringComparison.Ordinal);

        // The next valid reload applies again.
        rig.Config.Set(new() { ["Goldpath:Campaign:StaleClaimAfter"] = "00:04:00" });
        Assert.Equal(TimeSpan.FromMinutes(4), rig.Options.StaleClaimAfter);
        Assert.Equal(CodeLadder, rig.Options.RetryBackoff);
        Assert.Equal(1, refusals.Count);
    }

    [Fact]
    public void An_invalid_section_at_startup_is_refused_too_and_the_code_values_hold()
    {
        using var refusals = new RefusalCounter();
        using var rig = new Rig(configuration: new() { ["Goldpath:Campaign:RetryJitter"] = "2" });

        Assert.Equal(0.2, rig.Options.RetryJitter);
        Assert.Equal(1, refusals.Count);
    }

    [Fact]
    public void Configuration_is_read_invariantly_whatever_the_host_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            using var rig = new Rig(configuration: new() { ["Goldpath:Campaign:RetryJitter"] = "0.5" });
            Assert.Equal(0.5, rig.Options.RetryJitter);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void An_empty_scalar_reads_as_unset_and_the_code_value_holds()
    {
        using var rig = new Rig(configuration: new() { ["Goldpath:Campaign:RetryJitter"] = "", ["Goldpath:Campaign:StaleClaimAfter"] = "" });

        Assert.Equal(0.2, rig.Options.RetryJitter);
        Assert.Equal(TimeSpan.FromMinutes(10), rig.Options.StaleClaimAfter);
        Assert.Empty(rig.Logs.Warnings);
    }

    [Fact]
    public void GlobalTps_the_tick_the_slice_and_the_batch_sizes_are_not_reloaded()
    {
        using var rig = new Rig(o => o.GlobalTps = 100, new()
        {
            ["Goldpath:Campaign:GlobalTps"] = "5",
            ["Goldpath:Campaign:LeaderTick"] = "00:00:01",
            ["Goldpath:Campaign:LeadershipSlice"] = "00:00:05",
            ["Goldpath:Campaign:EnumerationBatchSize"] = "7",
            ["Goldpath:Campaign:ReleaseBatchSize"] = "7",
        });
        rig.Config.Set(new() { ["Goldpath:Campaign:GlobalTps"] = "6", ["Goldpath:Campaign:LeaderTick"] = "00:00:02" });

        Assert.Equal(100, rig.Options.GlobalTps);
        Assert.Equal(TimeSpan.FromMilliseconds(250), rig.Options.LeaderTick);
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Options.LeadershipSlice);
        Assert.Equal(2_000, rig.Options.EnumerationBatchSize);
        Assert.Equal(500, rig.Options.ReleaseBatchSize);
    }

    /// <summary>
    /// The end of the chain: the engine, driven with the instance the host wired, schedules a failed
    /// item's next attempt on the rung a reload set — not the code rung it started with.
    /// </summary>
    [Fact]
    public async Task The_engine_schedules_the_next_attempt_on_the_reloaded_rung()
    {
        using var rig = new Rig(o => o.RetryJitter = 0);
        using var fixture = new CampaignFixture(o => o.LeaderTick = TimeSpan.FromMilliseconds(20), sourceSize: 1, options: rig.Options);
        fixture.FailIds.Add(1);
        var campaign = await fixture.CreateAsync(R2Tests.Generous(maxAttempts: 3));
        await fixture.EnumerateAllAsync(campaign);

        rig.Config.Set(new() { ["Goldpath:Campaign:RetryBackoff:0"] = "03:00:00" });

        using (var scope = fixture.Services.CreateScope())
        {
            await fixture.Engine.ReleaseBatchAsync(scope.ServiceProvider, campaign, 1, CancellationToken.None);
        }

        var before = DateTimeOffset.UtcNow;
        await fixture.ConsumeAllPublishedAsync();
        var after = DateTimeOffset.UtcNow;

        var item = fixture.Query(db => db.Set<GoldpathCampaignItem>().AsNoTracking().Single());
        Assert.Equal(GoldpathCampaignItemState.AwaitingRetry, item.State);
        Assert.NotNull(item.NextAttemptAt);
        Assert.InRange(item.NextAttemptAt!.Value, before + TimeSpan.FromHours(3), after + TimeSpan.FromHours(3));
    }

    /// <summary>A host composed by <c>AddGoldpathCampaign</c> over a configuration source the test reloads.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(Action<GoldpathCampaignOptions>? configure = null, Dictionary<string, string?>? configuration = null)
        {
            Config = new ReloadableSource(configuration ?? []);
            var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            ((IConfigurationBuilder)builder.Configuration).Add(Config);
            builder.Services.AddLogging(logging => logging.AddProvider(Logs));
            builder.AddGoldpathCampaign<HostApplicationBuilder, CampaignTestContext>(o => configure?.Invoke(o));
            Host = builder.Build();
            Options = Host.Services.GetRequiredService<GoldpathCampaignOptions>();
        }

        public ReloadableSource Config { get; }

        public CapturingLogs Logs { get; } = new();

        public IHost Host { get; }

        public GoldpathCampaignOptions Options { get; }

        public void Dispose() => Host.Dispose();
    }

    /// <summary>An in-memory configuration source whose whole content a test replaces, firing a reload.</summary>
    private sealed class ReloadableSource(Dictionary<string, string?> initial) : IConfigurationSource
    {
        private readonly Provider _provider = new(initial);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

        public void Set(Dictionary<string, string?> data) => _provider.Replace(data);

        private sealed class Provider(Dictionary<string, string?> initial) : ConfigurationProvider
        {
            public override void Load() => Data = new Dictionary<string, string?>(initial, StringComparer.OrdinalIgnoreCase);

            public void Replace(Dictionary<string, string?> data)
            {
                Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
                OnReload();
            }
        }
    }

    /// <summary>Counts <c>goldpath_campaign_tuning_reload_refused_total</c> while it lives.</summary>
    private sealed class RefusalCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _count;

        public RefusalCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Goldpath.Campaign" && instrument.Name == "goldpath_campaign_tuning_reload_refused_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _count, value));
            _listener.Start();
        }

        public long Count => Interlocked.Read(ref _count);

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>Keeps the warnings the campaign logger writes.</summary>
    private sealed class CapturingLogs : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLogs owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning && category == "Goldpath.Campaign")
                {
                    lock (owner.Warnings)
                    {
                        owner.Warnings.Add(formatter(state, exception));
                    }
                }
            }
        }
    }
}
