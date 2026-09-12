using SolastaBot.ChainCollector;

namespace SolastaBot.Tests.ChainCollector;

public sealed class CollectorPolicyTests
{
    [Fact]
    public void PollScheduleMatchesTheOriginalCollector()
    {
        CollectorOptions options = new();

        AssertInterval(3, 0d, options.Schedule);
        AssertInterval(3, 119.99d, options.Schedule);
        AssertInterval(15, 120d, options.Schedule);
        AssertInterval(15, 599.99d, options.Schedule);
        AssertInterval(60, 600d, options.Schedule);
        AssertInterval(60, 3599.99d, options.Schedule);
        AssertInterval(300, 3600d, options.Schedule);
        AssertInterval(300, 10799.99d, options.Schedule);

        Assert.Null(CollectorPolicy.GetPollIntervalSeconds(10800d, options.Schedule));

        Assert.Equal(146, options.PollsPerMint);
    }

    [Fact]
    public void ThresholdWaitsForEnoughSamples()
    {
        CollectorOptions options = new();

        List<decimal> baseline = [.. Enumerable.Repeat(30m, 39)];

        decimal? threshold = CollectorPolicy.CalculateThreshold(baseline, options.BaselineMinimum, options.BuyInRatio);

        Assert.Null(threshold);
    }

    /// <summary>
    /// The learned rule must land back on the pre-registered constant when it sees the curve that
    /// constant was derived from. That agreement is the evidence the rule generalises rather than
    /// fits, so this asserts it directly.
    /// </summary>
    /// <remarks>
    /// To ten decimal places, not exactly. The ratio 31.04/30 does not terminate, so a decimal holds
    /// it truncated and multiplying back by 30 lands a few 1e-27 away. The threshold is only ever
    /// used as a comparison bound against a reserve balance, where that difference cannot change an
    /// outcome, and demanding bit-exactness here would assert something the rule never claimed.
    /// </remarks>
    [Fact]
    public void SolBaselineReproducesTheOriginalThreshold()
    {
        CollectorOptions options = new();

        List<decimal> baseline = [.. Enumerable.Repeat(30m, 40)];

        decimal? threshold = CollectorPolicy.CalculateThreshold(baseline, options.BaselineMinimum, options.BuyInRatio);

        Assert.True(threshold.HasValue);

        Assert.Equal(31.04m, threshold.Value, 10);
    }

    [Fact]
    public void FilterRequiresBothSocialSignalsAndReserveAboveThreshold()
    {
        CollectorOptions options = new();

        Assert.Null(CollectorPolicy.Classify(options.SolQuoteMint, false, true, 32m, 31.04m, options.SolQuoteMint));
        Assert.Null(CollectorPolicy.Classify(options.SolQuoteMint, true, false, 32m, 31.04m, options.SolQuoteMint));
        Assert.Null(CollectorPolicy.Classify(options.SolQuoteMint, true, true, 31.04m, 31.04m, options.SolQuoteMint));

        Assert.Equal("filtered_sol", CollectorPolicy.Classify(options.SolQuoteMint, true, true, 32m, 31.04m, options.SolQuoteMint));
        Assert.Equal("filtered_alt", CollectorPolicy.Classify("pump-quote", true, true, 1100m, 1070m, options.SolQuoteMint));
    }

    [Fact]
    public void DefaultAdmissionRatesPreserveAllThreeArms()
    {
        CollectorOptions options = new();

        Assert.Equal(1d, options.AdmissionRates["filtered_sol"]);

        Assert.Equal(0.40d, options.AdmissionRates["filtered_alt"]);

        Assert.Equal(1d, options.AdmissionRates["control"]);

        Assert.Equal(1d / 890d, options.ControlRate);
    }

    private static void AssertInterval(int expected, double ageSeconds, IReadOnlyList<PollScheduleEntry> schedule)
    {
        int? actual = CollectorPolicy.GetPollIntervalSeconds(ageSeconds, schedule);

        Assert.True(actual.HasValue);
        Assert.Equal(expected, actual.Value);
    }
}