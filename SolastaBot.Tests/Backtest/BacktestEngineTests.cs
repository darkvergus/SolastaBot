using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;

namespace SolastaBot.Tests.Backtest;

/// <summary>
/// Engine behaviour against scripted decisions, where every figure below is worked out by hand.
/// </summary>
/// <remarks>
/// The arithmetic is deliberately in round numbers. A position of exactly ten units entered at
/// exactly 100 and closed at exactly 120 makes a wrong fee, a missed funding settlement or a fill at
/// the wrong price visible as a difference a reader can check, rather than as a plausible-looking
/// number nobody can verify.
/// </remarks>
public sealed class BacktestEngineTests
{
    private static Instrument Instrument { get; } = new(
        Symbol: "TESTUSDT",
        BaseAsset: "TEST",
        QuoteAsset: "USDT",
        TickSize: 0.1m,
        StepSize: 0.001m,
        MinQuantity: 0.001m,
        MinNotional: 1m,
        MaxLeverage: 20);

    /// <summary>Risk sized so the worked example comes out at exactly ten units.</summary>
    private static RiskOptions Risk { get; } = new()
    {
        RiskFractionPerTrade = 0.01m,
        MaxLeverage = 3,
        DailyLossLimitFraction = 0.5m,
        MaxConsecutiveLosses = 100,
        LiquidationBufferAtrMultiple = 4m,
        MaintenanceMarginRate = 0.004m
    };

    private static Candle Bar(int hour, decimal open, decimal high, decimal low, decimal close) =>
        new(CandleFactory.Origin.AddHours(hour), open, high, low, close, 1m);

    private static StrategyDecision Long(decimal stopDistance) =>
        new(PositionSide.Long, stopDistance, DecisionReason.EnterLong);

    private static StrategyDecision Hold(decimal stopDistance) =>
        new(PositionSide.Long, stopDistance, DecisionReason.Hold);

    private static BacktestResult Run(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<StrategyDecision> script,
        BacktestOptions? options = null,
        IReadOnlyList<FundingEvent>? funding = null,
        RiskOptions? risk = null) =>
        new BacktestEngine().Run(new BacktestRequest
        {
            Instrument = Instrument,
            Candles = candles,
            Funding = funding ?? [],
            Strategy = new ScriptedStrategy(script),
            Risk = new RiskGate(risk ?? Risk),
            Options = options ?? new BacktestOptions
            {
                StartingBalance = 10_000m,
                Fees = FeeSchedule.Free,
                Slippage = BasisPointSlippage.None
            }
        });

    /// <summary>A long entered at 100 and closed at 120, with no costs of any kind.</summary>
    private static Candle[] WinningTrade() =>
    [
        Bar(0, 100m, 101m, 99m, 100m),
        Bar(1, 100m, 112m, 99.5m, 110m),
        Bar(2, 110m, 121m, 109m, 110m),
        Bar(3, 120m, 121m, 119m, 120m),
        Bar(4, 120m, 121m, 119m, 120m)
    ];

    private static StrategyDecision[] LongThenFlat() =>
    [
        Long(10m),
        Hold(10m),
        StrategyDecision.Flat(DecisionReason.ExitOnCross),
        StrategyDecision.Flat(DecisionReason.Hold),
        StrategyDecision.Flat(DecisionReason.Hold)
    ];

    [Fact]
    public void AFrictionlessRoundTripProducesExactlyTheHandComputedResult()
    {
        BacktestResult result = Run(WinningTrade(), LongThenFlat());

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(PositionSide.Long, trade.Side);
        Assert.Equal(10m, trade.Quantity);          // 1% of 10,000 equity, over a stop 10 wide
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Equal(120m, trade.ExitPrice);
        Assert.Equal(200m, trade.GrossPnl);
        Assert.Equal(0m, trade.Fees);
        Assert.Equal(0m, trade.Funding);
        Assert.Equal(200m, trade.NetPnl);
        Assert.Equal(ExitReason.Signal, trade.Reason);
        Assert.Equal(10_200m, result.Metrics.FinalEquity);
        Assert.Equal(0, result.Metrics.LiquidationCount);
    }

    /// <summary>
    /// The decisive look-ahead test. The signal bar closes at 100 and the next bar opens at 150. An
    /// engine that filled at the signal bar's close would report an entry at 100 and a fat profit.
    /// </summary>
    [Fact]
    public void OrdersFillAtTheNextBarOpenNeverAtTheSignalBarClose()
    {
        Candle[] candles =
        [
            Bar(0, 100m, 101m, 99m, 100m),
            Bar(1, 150m, 152m, 149m, 150m),
            Bar(2, 150m, 151m, 149m, 150m),
            Bar(3, 160m, 161m, 159m, 160m),
            Bar(4, 160m, 161m, 159m, 160m)
        ];

        BacktestResult result = Run(candles, LongThenFlat());

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(150m, trade.EntryPrice);
        Assert.Equal(160m, trade.ExitPrice);
    }

    [Fact]
    public void TakerFeesAreChargedOnBothSidesOfTheRoundTrip()
    {
        BacktestOptions options = new()
        {
            StartingBalance = 10_000m,
            Fees = FeeSchedule.BinanceUsdFutures,   // 5 basis points taker
            Slippage = BasisPointSlippage.None
        };

        BacktestResult result = Run(WinningTrade(), LongThenFlat(), options);

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(1.1m, trade.Fees);             // 10 * 100 * 0.0005 plus 10 * 120 * 0.0005
        Assert.Equal(200m, trade.GrossPnl);
        Assert.Equal(198.9m, trade.NetPnl);
        Assert.Equal(10_198.9m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void SlippageMovesBothFillsAgainstTheTaker()
    {
        BacktestOptions options = new()
        {
            StartingBalance = 10_000m,
            Fees = FeeSchedule.Free,
            Slippage = new BasisPointSlippage("test", 10m)
        };

        BacktestResult result = Run(WinningTrade(), LongThenFlat(), options);

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(100.1m, trade.EntryPrice);     // bought 10 basis points higher
        Assert.Equal(119.88m, trade.ExitPrice);     // sold 10 basis points lower
        Assert.Equal(197.8m, trade.GrossPnl);
    }

    /// <summary>
    /// Funding is charged against the position held through the settlement, and reported on the
    /// trade separately from fees so its cost cannot hide inside a net figure.
    /// </summary>
    [Fact]
    public void FundingIsChargedToAnOpenLongAndReportedSeparately()
    {
        FundingEvent[] funding = [new(CandleFactory.Origin.AddHours(2), 0.0001m)];

        BacktestResult result = Run(WinningTrade(), LongThenFlat(), funding: funding);

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(0.11m, trade.Funding);          // 10 units * 110 open * 0.01%
        Assert.Equal(200m, trade.GrossPnl);
        Assert.Equal(199.89m, trade.NetPnl);
        Assert.Equal(0.11m, result.Metrics.TotalFunding);
        Assert.Equal(10_199.89m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void FundingOutsideThePositionsLifetimeIsNotCharged()
    {
        // Hour zero precedes the entry fill, which happens at the open of hour one.
        FundingEvent[] funding = [new(CandleFactory.Origin, 0.01m)];

        BacktestResult result = Run(WinningTrade(), LongThenFlat(), funding: funding);

        Assert.Equal(0m, Assert.Single(result.Trades).Funding);
    }

    [Fact]
    public void DisablingFundingChangesNothingElseAboutTheResult()
    {
        FundingEvent[] funding = [new(CandleFactory.Origin.AddHours(2), 0.0001m)];
        BacktestOptions without = new()
        {
            StartingBalance = 10_000m,
            Fees = FeeSchedule.Free,
            Slippage = BasisPointSlippage.None,
            ApplyFunding = false
        };

        BacktestResult result = Run(WinningTrade(), LongThenFlat(), without, funding);

        Assert.Equal(0m, Assert.Single(result.Trades).Funding);
        Assert.Equal(10_200m, result.Metrics.FinalEquity);
    }

    [Fact]
    public void AStopInsideTheBarsRangeFillsAtTheStopPrice()
    {
        Candle[] candles =
        [
            Bar(0, 100m, 101m, 99m, 100m),
            Bar(1, 100m, 101m, 99m, 100m),
            Bar(2, 100m, 101m, 89m, 95m),          // low of 89 reaches the stop at 90
            Bar(3, 95m, 96m, 94m, 95m),
            Bar(4, 95m, 96m, 94m, 95m)
        ];

        BacktestResult result = Run(candles, LongThenFlat());

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.Reason);
        Assert.Equal(90m, trade.ExitPrice);
        Assert.Equal(-100m, trade.GrossPnl);
    }

    /// <summary>
    /// A bar that opens beyond the stop gapped through it overnight. Filling at the stop price would
    /// invent liquidity that was never there, so the fill is the open.
    /// </summary>
    [Fact]
    public void AStopGappedThroughFillsAtTheOpenNotTheStop()
    {
        Candle[] candles =
        [
            Bar(0, 100m, 101m, 99m, 100m),
            Bar(1, 100m, 101m, 99m, 100m),
            Bar(2, 85m, 86m, 84m, 85m),            // opened 5 below the stop
            Bar(3, 85m, 86m, 84m, 85m),
            Bar(4, 85m, 86m, 84m, 85m)
        ];

        BacktestResult result = Run(candles, LongThenFlat());

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.Reason);
        Assert.Equal(85m, trade.ExitPrice);
        Assert.Equal(-150m, trade.GrossPnl);
    }

    /// <summary>
    /// After a stop-out the same direction is refused until the trend has flipped and come back,
    /// even though the script keeps asking for it. Without this the strategy re-opens on the next
    /// bar and pays to be stopped out by the same chop repeatedly.
    /// </summary>
    [Fact]
    public void TheSameDirectionIsNotReEnteredImmediatelyAfterAStop()
    {
        Candle[] candles =
        [
            Bar(0, 100m, 101m, 99m, 100m),
            Bar(1, 100m, 101m, 99m, 100m),
            Bar(2, 100m, 101m, 89m, 95m),
            Bar(3, 95m, 96m, 94m, 95m),
            Bar(4, 95m, 96m, 94m, 95m),
            Bar(5, 95m, 96m, 94m, 95m)
        ];

        StrategyDecision[] script = [Long(10m), Hold(10m), Long(10m), Long(10m), Long(10m), Long(10m)];

        BacktestResult result = Run(candles, script);

        Assert.Single(result.Trades);
        Assert.Equal(ExitReason.StopLoss, result.Trades[0].Reason);
    }

    [Fact]
    public void AnOpenPositionIsClosedAtTheEndOfTheData()
    {
        StrategyDecision[] script = [Long(10m), Hold(10m), Hold(10m), Hold(10m), Hold(10m)];

        BacktestResult result = Run(WinningTrade(), script);

        TradeRecord trade = Assert.Single(result.Trades);
        Assert.Equal(ExitReason.EndOfData, trade.Reason);
        Assert.Equal(120m, trade.ExitPrice);
    }

    [Fact]
    public void AnExistingPositionIsNeverResizedWhileItsDirectionIsUnchanged()
    {
        // Every bar asks for a long, with a widening stop that would imply a different size each time.
        StrategyDecision[] script = [Long(10m), Long(5m), Long(2m), Long(1m), Long(20m)];

        BacktestResult result = Run(WinningTrade(), script);

        Assert.Single(result.Trades);
        Assert.Equal(10m, result.Trades[0].Quantity);
    }

    [Fact]
    public void ABacktestNeedsAtLeastTwoBars()
    {
        Assert.Throws<ArgumentException>(() => Run([Bar(0, 1m, 1m, 1m, 1m)], [Long(1m)]));
    }
}
