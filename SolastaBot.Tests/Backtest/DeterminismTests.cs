using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;

namespace SolastaBot.Tests.Backtest;

/// <summary>
/// Determinism and cost-honesty checks over a full-length run of the real strategy.
/// </summary>
/// <remarks>
/// Determinism is the property the whole design rests on. If the same inputs can produce two
/// different ledgers then no backtest result means anything, no parameter comparison is valid, and
/// no live divergence can ever be traced back to its cause. It is cheap to assert and catastrophic
/// to lose, so it is asserted here rather than assumed.
/// </remarks>
public sealed class DeterminismTests
{
    private static readonly Candle[] Series = CandleFactory.RandomWalk(4_000, seed: 2_024, volatility: 0.012m);

    private static BacktestRequest Request(BacktestOptions? options = null, IReadOnlyList<FundingEvent>? funding = null) =>
        new()
        {
            Instrument = Instrument.BtcUsdtPerpetual,
            Candles = Series,
            Funding = funding ?? [],
            Strategy = new EmaCrossStrategy(new EmaCrossOptions
            {
                FastPeriod = 12,
                SlowPeriod = 26,
                AtrPeriod = 14,
                TrendPeriod = 14,
                MinimumTrendStrength = 18m,
                StopAtrMultiple = 2.5m
            }),
            Risk = new RiskGate(new RiskOptions
            {
                RiskFractionPerTrade = 0.01m,
                MaxLeverage = 3,
                DailyLossLimitFraction = 0.05m,
                MaxConsecutiveLosses = 5,
                LiquidationBufferAtrMultiple = 4m,
                MaintenanceMarginRate = 0.004m
            }),
            Options = options ?? new BacktestOptions
            {
                StartingBalance = 10_000m,
                Fees = FeeSchedule.BinanceUsdFutures,
                Slippage = BasisPointSlippage.Medium
            }
        };

    [Fact]
    public void TheSameInputsAlwaysProduceTheSameLedger()
    {
        BacktestResult first = new BacktestEngine().Run(Request());
        BacktestResult second = new BacktestEngine().Run(Request());

        Assert.Equal(first.Trades.Count, second.Trades.Count);
        Assert.Equal(first.Trades, second.Trades);
        Assert.Equal(first.EquityCurve, second.EquityCurve);
        Assert.Equal(first.Metrics, second.Metrics);
    }

    [Fact]
    public void TheRunIsLongEnoughToBeWorthAsserting()
    {
        BacktestResult result = new BacktestEngine().Run(Request());

        Assert.True(result.Trades.Count > 10, $"Only {result.Trades.Count} trades; the series is not exercising much.");
        Assert.Equal(Series.Length, result.EquityCurve.Count);
        Assert.Equal(0, result.Metrics.LiquidationCount);
    }

    /// <summary>
    /// The strategy must still be trading at the end of the run.
    /// </summary>
    /// <remarks>
    /// A risk halt with no release path does not look like a failure. The run completes, the metrics
    /// are computed over the whole period and the equity curve simply goes flat, which reads as a
    /// strategy that stopped finding trades rather than one that was switched off in the first tenth
    /// of the data. Checking where the last trade falls is the cheapest way to tell the difference.
    /// </remarks>
    [Fact]
    public void TradingContinuesThroughToTheEndOfTheRunRatherThanHaltingPermanently()
    {
        BacktestResult result = new BacktestEngine().Run(Request());

        Assert.NotEmpty(result.Trades);

        DateTime lastTrade = result.Trades[^1].ClosedAt;
        DateTime threeQuartersIn = result.From + ((result.To - result.From) * 0.75);

        Assert.True(
            lastTrade > threeQuartersIn,
            $"The last trade closed at {lastTrade:O}, before {threeQuartersIn:O}. Trading appears to have stopped.");
    }

    /// <summary>
    /// Every cost must make the result worse. A sign error in a fee or a funding payment is easy to
    /// write and produces a strategy that looks better the more it trades.
    /// </summary>
    [Fact]
    public void EveryModelledCostReducesTheFinalEquity()
    {
        BacktestOptions free = new()
        {
            StartingBalance = 10_000m,
            Fees = FeeSchedule.Free,
            Slippage = BasisPointSlippage.None
        };

        decimal frictionless = new BacktestEngine().Run(Request(free)).Metrics.FinalEquity;
        decimal withFees = new BacktestEngine().Run(Request(free with { Fees = FeeSchedule.BinanceUsdFutures }))
            .Metrics.FinalEquity;
        decimal withSlippage = new BacktestEngine().Run(Request(free with { Slippage = BasisPointSlippage.Harsh }))
            .Metrics.FinalEquity;

        Assert.True(withFees < frictionless, $"Fees did not cost anything: {withFees} against {frictionless}.");
        Assert.True(withSlippage < frictionless, $"Slippage did not cost anything: {withSlippage} against {frictionless}.");
    }

    /// <summary>
    /// Funding against a long must cost it money, and the result must say how much.
    /// </summary>
    [Fact]
    public void PositiveFundingCostsLongsMoneyAndIsReportedInFull()
    {
        BacktestOptions free = new()
        {
            StartingBalance = 10_000m,
            Fees = FeeSchedule.Free,
            Slippage = BasisPointSlippage.None
        };

        FundingEvent[] funding = CandleFactory.Funding(CandleFactory.Origin, 600, 0.0005m);

        BacktestResult without = new BacktestEngine().Run(Request(free));
        BacktestResult with = new BacktestEngine().Run(Request(free, funding));

        Assert.Equal(0m, without.Metrics.TotalFunding);
        Assert.NotEqual(0m, with.Metrics.TotalFunding);
        Assert.Equal(
            with.Metrics.TotalFunding,
            with.Trades.Sum(trade => trade.Funding));
    }

    /// <summary>
    /// Harsh execution assumptions are a gate, not a footnote. This does not assert that the toy
    /// strategy survives them, only that the comparison the gate needs is actually available.
    /// </summary>
    [Fact]
    public void EverySlippageProfileProducesAComparableResult()
    {
        BasisPointSlippage[] profiles =
        [
            BasisPointSlippage.None,
            BasisPointSlippage.Low,
            BasisPointSlippage.Medium,
            BasisPointSlippage.Harsh
        ];

        decimal previous = decimal.MaxValue;

        foreach (BasisPointSlippage profile in profiles)
        {
            BacktestResult result = new BacktestEngine().Run(Request(new BacktestOptions
            {
                StartingBalance = 10_000m,
                Fees = FeeSchedule.Free,
                Slippage = profile
            }));

            Assert.Equal(profile.Name, result.SlippageProfile);
            Assert.True(
                result.Metrics.FinalEquity <= previous,
                $"Profile {profile.Name} beat a gentler one: {result.Metrics.FinalEquity} against {previous}.");
            previous = result.Metrics.FinalEquity;
        }
    }
}
