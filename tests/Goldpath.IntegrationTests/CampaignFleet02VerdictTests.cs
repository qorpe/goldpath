using System.Globalization;
using Xunit;

namespace Goldpath.Tests.Integration;

/// <summary>
/// The S-FLEET-02 verdict against the thresholds signed on #326, without a container: the hour's
/// result is only as honest as this arithmetic, and an hour-long run is the wrong place to find out
/// that a boundary is off by one.
/// </summary>
public sealed class CampaignFleet02VerdictTests
{
    private const int Rate = 2_000;

    private static Fleet02Counts Clean(long published = 7_200_000, long stranded = 0)
        => new(published, stranded, published - stranded, published - stranded, published - stranded, 0);

    private static long[] Flat(int seconds, long perSecond) => Enumerable.Repeat(perSecond, seconds).ToArray();

    [Fact]
    public void Every_second_exactly_at_the_rate_passes_even_with_the_floor_at_the_rate()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(3_600, Rate), Rate, Rate, 8, Clean());

        Assert.Equal("PASS", v.Label);
        Assert.Equal(Rate, v.MeanTps);
        Assert.Equal(250, v.TpsPerCore);
        Assert.Equal(0, v.P95Deviation);
        Assert.Equal(3_600, v.WithinBand);
        Assert.True(v.PassRequiresEveryWindowExact);
    }

    [Fact]
    public void One_second_above_the_rate_fails_whatever_the_mean()
    {
        var series = Flat(3_600, Rate);
        series[1_800] = Rate + 1;

        var v = CampaignFleet02Verdict.Judge(series, Rate, Rate, 8, Clean());

        Assert.Equal("FAIL", v.Label);
        Assert.Equal(1, v.Above);
        Assert.False(v.MeetsCeiling);
        Assert.True(v.MeetsMean);
    }

    [Fact]
    public void A_mean_below_the_floor_is_a_hardware_ceiling_with_its_rate_per_core()
    {
        var series = Flat(3_600, Rate);
        series[0] = Rate - 1;

        var v = CampaignFleet02Verdict.Judge(series, Rate, Rate, 4, Clean());

        Assert.Equal("HARDWARE CEILING", v.Label);
        Assert.False(v.MeetsMean);
        Assert.True(v.MeetsJitter);
        Assert.Equal(v.MeanTps / 4, v.TpsPerCore);
    }

    [Fact]
    public void A_hardware_ceiling_that_also_lost_work_is_a_fail()
    {
        var counts = Clean() with { Sink = 7_199_999 };

        var v = CampaignFleet02Verdict.Judge(Flat(60, 1_000), Rate, Rate, 4, counts);

        Assert.Equal("FAIL", v.Label);
        Assert.Equal(1, v.Lost);
    }

    [Theory]
    [InlineData(95, "PASS")]    // exactly the p95: 95 of 100 windows in the band
    [InlineData(94, "FAIL")]    // one window short
    public void The_p95_band_holds_at_95_of_100_windows_and_not_at_94(int inBand, string label)
    {
        // Floor below the rate, so only the jitter decides; the out-of-band windows are slow, not bursts.
        var series = Enumerable.Repeat((long)Rate, inBand).Concat(Enumerable.Repeat(1_000L, 100 - inBand)).ToArray();

        var v = CampaignFleet02Verdict.Judge(series, Rate, 1_000, 8, Clean());

        Assert.Equal(label, v.Label);
        Assert.Equal(inBand, v.WithinBand);
    }

    [Fact]
    public void Exactly_five_percent_below_the_rate_is_inside_the_band_and_one_more_is_not()
    {
        var edge = CampaignFleet02Verdict.Judge(Flat(100, 1_900), Rate, 1_000, 8, Clean());
        var outside = CampaignFleet02Verdict.Judge(Flat(100, 1_899), Rate, 1_000, 8, Clean());

        Assert.Equal(100, edge.WithinBand);
        Assert.True(edge.MeetsJitter);
        Assert.Equal(0, outside.WithinBand);
        Assert.False(outside.MeetsJitter);
    }

    [Fact]
    public void Items_stranded_on_the_queue_by_the_pause_are_accounted_not_lost()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(60, Rate), Rate, Rate, 8, Clean(published: 120_000, stranded: 336));

        Assert.True(v.ExactlyOnce);
        Assert.Equal(0, v.Lost);
        Assert.Equal(0, v.Duplicated);
    }

    [Fact]
    public void A_target_executed_twice_is_duplicated()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(60, Rate), Rate, Rate, 8, Clean() with { ExecutedTwice = 1 });

        Assert.Equal("FAIL", v.Label);
        Assert.Equal(1, v.Duplicated);
    }

    [Fact]
    public void More_publishes_than_items_released_is_a_second_release()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(60, Rate), Rate, Rate, 8, Clean() with { Published = 7_200_001 });

        Assert.Equal(1, v.Duplicated);
        Assert.Equal(0, v.Lost);
        Assert.False(v.ExactlyOnce);
    }

    [Fact]
    public void An_item_released_with_no_publish_is_lost()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(60, Rate), Rate, Rate, 8, Clean() with { Published = 7_199_999 });

        Assert.Equal(1, v.Lost);
        Assert.Equal(0, v.Duplicated);
    }

    [Fact]
    public void A_terminal_row_whose_target_never_ran_is_lost()
    {
        var v = CampaignFleet02Verdict.Judge(Flat(60, Rate), Rate, Rate, 8, Clean() with { ExecutedTargets = 7_199_999 });

        Assert.Equal(1, v.Lost);
        Assert.Equal("FAIL", v.Label);
    }

    [Fact]
    public void Empty_seconds_are_counted_and_percentiles_read_the_series()
    {
        var series = Enumerable.Range(1, 100).Select(i => (long)(i == 1 ? 0 : 1_000 + i)).ToArray();

        var v = CampaignFleet02Verdict.Judge(series, Rate, 1_000, 8, Clean());

        Assert.Equal(1, v.Empty);
        Assert.Equal(1_005, v.RateP5);
        Assert.Equal(1_050, v.RateP50);
        Assert.Equal(1_095, v.RateP95);
    }

    [Fact]
    public void A_window_with_no_whole_second_or_a_nonpositive_rate_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CampaignFleet02Verdict.Judge([], Rate, Rate, 8, Clean()));
        Assert.Throws<ArgumentOutOfRangeException>(() => CampaignFleet02Verdict.Judge(Flat(1, Rate), 0, Rate, 8, Clean()));
        Assert.Throws<ArgumentOutOfRangeException>(() => CampaignFleet02Verdict.Judge(Flat(1, Rate), Rate, Rate, 0, Clean()));
    }

    [Fact]
    public void The_series_csv_is_invariant_under_a_turkish_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            var csv = CampaignFleet02Verdict.ToCsv([1_999, 2_000], new DateTimeOffset(2026, 3, 29, 0, 59, 59, TimeSpan.Zero));

            Assert.Equal("second_utc,released\n2026-03-29T00:59:59Z,1999\n2026-03-29T01:00:00Z,2000\n", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
