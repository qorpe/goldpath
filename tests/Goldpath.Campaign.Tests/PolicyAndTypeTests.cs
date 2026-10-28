using Xunit;

namespace Goldpath.Campaign.Tests;

public class PolicyTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    private static GoldpathCampaignPolicy Policy(TimeOnly? start, TimeOnly? end, string tz = "UTC")
        => new(Tps: 10, DailyQuota: null, MaxInFlight: 100, WindowStart: start, WindowEnd: end, TimeZoneId: tz);

    [Fact]
    public void NoWindowIsAlwaysOpen()
        => Assert.True(Policy(null, null).IsWindowOpen(Noon));

    [Fact]
    public void HalfWindowIsAlwaysOpen()
        => Assert.True(Policy(new TimeOnly(9, 0), null).IsWindowOpen(Noon.AddHours(-8)));

    [Theory]
    [InlineData(12, 0, true)]    // inclusive start
    [InlineData(13, 30, true)]
    [InlineData(17, 0, false)]   // exclusive end
    [InlineData(11, 59, false)]
    [InlineData(23, 0, false)]
    public void DayWindowBoundaries(int hour, int minute, bool open)
        => Assert.Equal(open, Policy(new TimeOnly(12, 0), new TimeOnly(17, 0))
            .IsWindowOpen(new DateTimeOffset(2026, 7, 9, hour, minute, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(22, 0, true)]    // inclusive start
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]     // past midnight
    [InlineData(6, 0, false)]    // exclusive end
    [InlineData(12, 0, false)]   // midday is outside an overnight window
    public void OvernightWindowBoundaries(int hour, int minute, bool open)
        => Assert.Equal(open, Policy(new TimeOnly(22, 0), new TimeOnly(6, 0))
            .IsWindowOpen(new DateTimeOffset(2026, 7, 9, hour, minute, 0, TimeSpan.Zero)));

    [Fact]
    public void WindowEvaluatesInThePolicyTimezone()
    {
        // 12:00 UTC is 15:00 in Istanbul — inside a 14:00–16:00 Istanbul window.
        var policy = Policy(new TimeOnly(14, 0), new TimeOnly(16, 0), "Europe/Istanbul");
        Assert.True(policy.IsWindowOpen(Noon));
        Assert.False(Policy(new TimeOnly(14, 0), new TimeOnly(16, 0), "UTC").IsWindowOpen(Noon));
    }

    [Fact]
    public void LocalDayCrossesMidnightInThePolicyTimezone()
    {
        // 22:30 UTC on the 9th is already the 10th in Istanbul (UTC+3).
        var evening = new DateTimeOffset(2026, 7, 9, 22, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 7, 10), Policy(null, null, "Europe/Istanbul").LocalDay(evening));
        Assert.Equal(new DateOnly(2026, 7, 9), Policy(null, null, "UTC").LocalDay(evening));
    }
}

public class TypeRegistrationTests
{
    [Fact]
    public void MissingCeilingRefusesToBake()
    {
        var options = new GoldpathCampaignOptions();
        var e = Assert.Throws<InvalidOperationException>(() =>
            options.AddCampaign<TestTarget>("winback", c => c.Targets((_, _) => Empty())));
        Assert.Contains("MaxTargets", e.Message, StringComparison.Ordinal);
        Assert.Contains("GP1701", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonPositiveCeilingIsRefused()
    {
        var options = new GoldpathCampaignOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            options.AddCampaign<TestTarget>("winback", c => c.MaxTargets(0)));
    }

    [Fact]
    public void MissingTargetsRefusesToBake()
    {
        var options = new GoldpathCampaignOptions();
        var e = Assert.Throws<InvalidOperationException>(() =>
            options.AddCampaign<TestTarget>("winback", c => c.MaxTargets(10)));
        Assert.Contains("no Targets selector", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateKeyIsRefused()
    {
        var options = Registered();
        var e = Assert.Throws<InvalidOperationException>(() =>
            options.AddCampaign<TestTarget>("winback", c => c.MaxTargets(10).Targets((_, _) => Empty())));
        Assert.Contains("already registered", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTypeLookupTeachesTheRegisteredKeys()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Registered().Type("winbck"));
        Assert.Contains("No campaign type named 'winbck'", e.Message, StringComparison.Ordinal);
        Assert.Contains("winback", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultPolicyGolden()
    {
        var policy = Registered().Type("winback").DefaultPolicy;
        Assert.Equal(50, policy.Tps);
        Assert.Null(policy.DailyQuota);
        Assert.Equal(1_000, policy.MaxInFlight);
        Assert.Null(policy.WindowStart);
        Assert.Null(policy.WindowEnd);
        Assert.Equal("UTC", policy.TimeZoneId);
    }

    [Fact]
    public void DefaultPolicyIsCustomizable()
    {
        var options = new GoldpathCampaignOptions();
        options.AddCampaign<TestTarget>("winback", c => c
            .MaxTargets(10).Targets((_, _) => Empty())
            .DefaultPolicy(p => p with { Tps = 7, DailyQuota = 42 }));
        var policy = options.Type("winback").DefaultPolicy;
        Assert.Equal(7, policy.Tps);
        Assert.Equal(42, policy.DailyQuota);
    }

    [Fact]
    public void OptionsGolden()
    {
        var options = new GoldpathCampaignOptions();
        Assert.Equal(2_000, options.EnumerationBatchSize);
        Assert.Equal(500, options.ReleaseBatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.LeaderTick);
        Assert.Equal(TimeSpan.FromSeconds(60), options.LeadershipSlice);   // #284: the default pacer cron's period, so leadership is continuous
        Assert.Equal(TimeSpan.FromMinutes(10), options.StaleClaimAfter);
        Assert.Empty(options.Types);
    }

    private static GoldpathCampaignOptions Registered()
    {
        var options = new GoldpathCampaignOptions();
        options.AddCampaign<TestTarget>("winback", c => c.MaxTargets(10).Targets((_, _) => Empty()));
        return options;
    }

    private static async IAsyncEnumerable<TestTarget> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
