using System;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;
using Xunit;

namespace SolastaBot.Tests.Risk;

public sealed class RiskGateTests
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

    private static RiskOptions Defaults { get; } = new()
    {
        RiskFractionPerTrade = 0.01m,
        MaxLeverage = 3,
        DailyLossLimitFraction = 0.03m,
        MaxConsecutiveLosses = 4,
        LiquidationBufferAtrMultiple = 4m,
        MaintenanceMarginRate = 0.004m
    };

    private static MarketSnapshot Snapshot(decimal close = 100m, PositionState? position = null) => new(Instrument, new(CandleFactory.Origin, close, close, close,
        close, 1m), position ?? PositionState.Flat);

    private static RiskLedger Ledger(RiskOptions? options = null, decimal equity = 10_000m)
    {
        RiskLedger ledger = new(options ?? Defaults);
        ledger.Observe(CandleFactory.Origin, equity);

        return ledger;
    }

    [Fact]
    public void SizeRisksExactlyTheConfiguredFractionOfEquityBetweenEntryAndStop()
    {
        RiskGate gate = new(Defaults);
        StrategyDecision decision = new(PositionSide.Long, 10m, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10_000m, 0m), Ledger());

        // 1% of 10,000 is 100 at risk; a stop 10 wide means ten units.
        Assert.Equal(RiskOutcome.Approved, verdict.Outcome);
        Assert.Equal(10m, verdict.Quantity);
        Assert.Equal(90m, verdict.StopPrice);
    }

    [Fact]
    public void AShortPutsItsStopAboveTheEntry()
    {
        RiskGate gate = new(Defaults);
        StrategyDecision decision = new(PositionSide.Short, 10m, DecisionReason.EnterShort);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10_000m, 0m), Ledger());

        Assert.Equal(PositionSide.Short, verdict.TargetSide);
        Assert.Equal(110m, verdict.StopPrice);
    }

    [Fact]
    public void TheLeverageCapShrinksTheSizeAndSaysSo()
    {
        RiskGate gate = new(Defaults);
        // A stop only 0.1 wide would ask for 1,000 units, which is 100,000 notional on 10,000 equity.
        StrategyDecision decision = new(PositionSide.Long, 0.1m, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10_000m, 0m), Ledger());

        Assert.Equal(RiskOutcome.Reduced, verdict.Outcome);
        Assert.Equal(300m, verdict.Quantity); // 10,000 equity * 3 leverage / 100 price
    }

    [Fact]
    public void UnrealisedProfitCountsTowardsTheEquityThatIsRisked()
    {
        RiskGate gate = new(Defaults);
        StrategyDecision decision = new(PositionSide.Long, 10m, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10_000m, 5_000m), Ledger());

        Assert.Equal(15m, verdict.Quantity); // 1% of 15,000 equity over a stop 10 wide
    }

    [Theory, InlineData(0), InlineData(-1)]
    public void ANonPositiveStopDistanceIsRefused(int stopDistance)
    {
        RiskGate gate = new(Defaults);
        StrategyDecision decision = new(PositionSide.Long, stopDistance, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10_000m, 0m), Ledger());

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.Equal(0m, verdict.Quantity);
    }

    [Fact]
    public void AnExhaustedAccountIsRefused()
    {
        RiskGate gate = new(Defaults);
        StrategyDecision decision = new(PositionSide.Long, 10m, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(0m, 0m), Ledger(equity: 0m));

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
    }

    [Fact]
    public void AnOrderTooSmallForTheExchangeFiltersIsRefused()
    {
        RiskGate gate = new(Defaults with { RiskFractionPerTrade = 0.0001m });
        StrategyDecision decision = new(PositionSide.Long, 1_000m, DecisionReason.EnterLong);

        RiskVerdict verdict = gate.Evaluate(decision, Snapshot(), new(10m, 0m), Ledger(equity: 10m));

        Assert.Equal(RiskOutcome.Rejected, verdict.Outcome);
        Assert.NotNull(verdict.Detail);
    }

    [Fact]
    public void AFlatTargetNeedsNoSizing()
    {
        RiskGate gate = new(Defaults);

        RiskVerdict verdict = gate.Evaluate(StrategyDecision.Flat(DecisionReason.Warmup), Snapshot(), new(10_000m, 0m), Ledger());

        Assert.Equal(RiskOutcome.NoPosition, verdict.Outcome);
    }

    [Fact]
    public void TheKillSwitchStopsEverything()
    {
        RiskGate gate = new(Defaults);
        RiskLedger ledger = Ledger();
        ledger.EngageKillSwitch();

        RiskVerdict verdict = gate.Evaluate(new(PositionSide.Long, 10m, DecisionReason.EnterLong), Snapshot(), new(10_000m, 0m), ledger);

        Assert.Equal(RiskOutcome.Halted, verdict.Outcome);
        Assert.Contains("Kill switch", verdict.Detail);
    }

    [Fact]
    public void ReachingTheDailyLossLimitHaltsTrading()
    {
        RiskGate gate = new(Defaults);
        RiskLedger ledger = Ledger();

        // Down 3% from the day's opening equity of 10,000.
        RiskVerdict verdict = gate.Evaluate(new(PositionSide.Long, 10m, DecisionReason.EnterLong), Snapshot(), new(9_700m, 0m), ledger);

        Assert.Equal(RiskOutcome.Halted, verdict.Outcome);
        Assert.Contains("Daily loss", verdict.Detail);
    }

    [Fact]
    public void CrossingIntoANewUtcDayLiftsADailyLossHalt()
    {
        RiskGate gate = new(Defaults);
        RiskLedger ledger = Ledger();
        Assert.NotNull(ledger.HaltReason(9_700m));

        ledger.Observe(CandleFactory.Origin.AddDays(1), 9_700m);

        Assert.Null(ledger.HaltReason(9_700m));

        Assert.Equal(RiskOutcome.Approved, gate.Evaluate(new(PositionSide.Long, 10m, DecisionReason.EnterLong), Snapshot(), new(9_700m, 0m), ledger).Outcome);
    }

    [Fact]
    public void EnoughConsecutiveLossesHaltTrading()
    {
        RiskGate gate = new(Defaults);
        RiskLedger ledger = Ledger();

        for (int index = 0; index < 4; index++)
        {
            ledger.RecordTrade(Loss());
        }

        RiskVerdict verdict = gate.Evaluate(new(PositionSide.Long, 10m, DecisionReason.EnterLong), Snapshot(), new(10_000m, 0m), ledger);

        Assert.Equal(RiskOutcome.Halted, verdict.Outcome);
        Assert.Contains("consecutive losses", verdict.Detail);
    }

    /// <summary>
    /// A halt that only a win can clear can never be cleared, because the halt prevents the trade
    /// that would clear it. This was a real bug: a five-year backtest lost four in a row in May 2021
    /// and never traded again, while still reporting a profit for the whole period.
    /// </summary>
    [Fact]
    public void AConsecutiveLossHaltIsLiftedByTheNextUtcDayNotOnlyByAWin()
    {
        RiskLedger ledger = Ledger();

        for (int index = 0; index < 4; index++)
        {
            ledger.RecordTrade(Loss());
        }

        Assert.NotNull(ledger.HaltReason(10_000m));

        ledger.Observe(CandleFactory.Origin.AddDays(1), 10_000m);

        Assert.Equal(0, ledger.ConsecutiveLosses);
        Assert.Null(ledger.HaltReason(10_000m));
    }

    [Fact]
    public void ObservingTheSameDayAgainDoesNotClearTheLossCount()
    {
        RiskLedger ledger = Ledger();
        ledger.RecordTrade(Loss());
        ledger.RecordTrade(Loss());

        ledger.Observe(CandleFactory.Origin.AddHours(6), 10_000m);

        Assert.Equal(2, ledger.ConsecutiveLosses);
    }

    [Fact]
    public void AWinResetsTheConsecutiveLossCount()
    {
        RiskLedger ledger = Ledger();
        ledger.RecordTrade(Loss());
        ledger.RecordTrade(Loss());
        ledger.RecordTrade(Win());

        Assert.Equal(0, ledger.ConsecutiveLosses);
        Assert.Null(ledger.HaltReason(10_000m));
    }

    /// <summary>
    /// The property that makes leverage survivable: for any approved position, price must travel
    /// further to liquidate than it does to hit the protective stop. If this ever fails, a normal
    /// losing trade becomes a wiped account.
    /// </summary>
    [Fact]
    public void LiquidationAlwaysSitsBeyondTheProtectiveStop()
    {
        decimal[] fractions = [0.001m, 0.005m, 0.01m, 0.05m, 0.1m];
        int[] leverages = [1, 2, 3, 5, 10, 20, 50];
        decimal[] stops = [0.05m, 0.5m, 2m, 10m, 50m, 500m];
        decimal[] prices = [1m, 100m, 5_000m, 90_000m];
        decimal[] equities = [500m, 10_000m, 1_000_000m];
        PositionSide[] sides = [PositionSide.Long, PositionSide.Short];
        int approved = 0;

        foreach (decimal fraction in fractions)
        {
            foreach (int leverage in leverages)
            {
                RiskGate gate = new(Defaults with { RiskFractionPerTrade = fraction, MaxLeverage = leverage });

                foreach (decimal stop in stops)
                {
                    foreach (decimal price in prices)
                    {
                        foreach (decimal equity in equities)
                        {
                            foreach (PositionSide side in sides)
                            {
                                RiskVerdict verdict = gate.Evaluate(
                                    new(side, stop, DecisionReason.EnterLong),
                                    Snapshot(price),
                                    new(equity, 0m),
                                    Ledger(equity: equity));

                                if (!verdict.WantsPosition)
                                {
                                    continue;
                                }

                                approved++;
                                decimal stopTravel = Math.Abs(price - verdict.StopPrice);

                                decimal liquidationTravel = verdict.LiquidationPrice <= 0m ? decimal.MaxValue : Math.Abs(price - verdict.LiquidationPrice);

                                Assert.True(liquidationTravel > stopTravel, $"At {fraction:P1} risk, {leverage}x cap, stop {stop} and price {price}, " +
                                                                            $"liquidation is {liquidationTravel} away but the stop is {stopTravel}.");
                            }
                        }
                    }
                }
            }
        }

        Assert.True(approved > 500, $"Only {approved} positions were approved; the sweep proves little.");
    }

    private static TradeRecord Loss() => Trade(-50m);

    private static TradeRecord Win() => Trade(120m);

    private static TradeRecord Trade(decimal grossPnl) => new(Symbol: "TESTUSDT", Side: PositionSide.Long, OpenedAt: CandleFactory.Origin, ClosedAt: CandleFactory.Origin.AddHours(1),
        Quantity: 1m, EntryPrice: 100m, ExitPrice: 100m + grossPnl, GrossPnl: grossPnl, Fees: 0m, Funding: 0m, Reason: ExitReason.Signal);
}