using SolastaBot.Core.Domain;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;

namespace SolastaBot.Tests.Strategy;

public sealed class EmaCrossStrategyTests
{
    private static Instrument Instrument { get; } = Instrument.BtcUsdtPerpetual;

    private static EmaCrossOptions Fast { get; } = new()
    {
        FastPeriod = 5,
        SlowPeriod = 12,
        AtrPeriod = 5,
        TrendPeriod = 5,
        MinimumTrendStrength = 20m,
        StopAtrMultiple = 2m
    };

    private static List<StrategyDecision> Feed(IStrategy strategy, IReadOnlyList<Candle> candles)
    {
        List<StrategyDecision> decisions = [];
        PositionState position = PositionState.Flat;

        foreach (Candle candle in candles)
        {
            StrategyDecision decision = strategy.Evaluate(new MarketSnapshot(Instrument, candle, position));
            decisions.Add(decision);

            // Mimic the engine closely enough that hold and exit paths are exercised.
            position = decision.TargetSide == PositionSide.Flat
                ? PositionState.Flat
                : new PositionState
                {
                    Side = decision.TargetSide,
                    Quantity = 1m,
                    EntryPrice = candle.Close,
                    StopPrice = candle.Close - decision.StopDistance
                };
        }

        return decisions;
    }

    [Fact]
    public void FeedingTheSameBarTwiceThrowsRatherThanCorruptingTheIndicators()
    {
        EmaCrossStrategy strategy = new(Fast);
        Candle candle = CandleFactory.Trend(1, 100m, 1m)[0];
        MarketSnapshot snapshot = new(Instrument, candle, PositionState.Flat);

        strategy.Evaluate(snapshot);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => strategy.Evaluate(snapshot));
        Assert.Contains("ascending order", error.Message);
    }

    [Fact]
    public void FeedingBarsOutOfOrderThrows()
    {
        EmaCrossStrategy strategy = new(Fast);
        Candle[] candles = CandleFactory.Trend(3, 100m, 1m);

        strategy.Evaluate(new MarketSnapshot(Instrument, candles[2], PositionState.Flat));

        Assert.Throws<InvalidOperationException>(
            () => strategy.Evaluate(new MarketSnapshot(Instrument, candles[1], PositionState.Flat)));
    }

    [Fact]
    public void NothingIsProposedUntilEveryIndicatorHasWarmedUp()
    {
        EmaCrossStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(60, 100m, 1m));

        for (int index = 0; index < strategy.WarmupBars - 1; index++)
        {
            Assert.Equal(DecisionReason.Warmup, decisions[index].Reason);
            Assert.Equal(PositionSide.Flat, decisions[index].TargetSide);
        }
    }

    [Fact]
    public void ASustainedRiseIsEventuallyTakenLongWithAStopBelow()
    {
        EmaCrossStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 100m, 1m));

        StrategyDecision entry = Assert.Single(decisions, decision => decision.Reason == DecisionReason.EnterLong);
        Assert.Equal(PositionSide.Long, entry.TargetSide);
        Assert.True(entry.StopDistance > 0m, "An entry must carry a positive stop distance.");
    }

    [Fact]
    public void ASustainedFallIsEventuallyTakenShort()
    {
        EmaCrossStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 300m, -1m));

        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.EnterShort);
    }

    [Fact]
    public void ShortsAreNeverProposedWhenTheyAreDisallowed()
    {
        EmaCrossStrategy strategy = new(Fast with { AllowShorts = false });
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 300m, -1m));

        Assert.DoesNotContain(decisions, decision => decision.TargetSide == PositionSide.Short);
    }

    [Fact]
    public void AnImpossiblyHighTrendFloorBlocksEveryEntry()
    {
        EmaCrossStrategy strategy = new(Fast with { MinimumTrendStrength = 1_000m });
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.RandomWalk(400, seed: 5));

        Assert.DoesNotContain(decisions, decision => decision.Reason is DecisionReason.EnterLong or DecisionReason.EnterShort);
        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.TrendTooWeak);
    }

    [Fact]
    public void ResetRewindsTheStrategyCompletely()
    {
        EmaCrossStrategy strategy = new(Fast);
        Candle[] candles = CandleFactory.RandomWalk(300, seed: 88);

        List<StrategyDecision> first = Feed(strategy, candles);
        strategy.Reset();
        List<StrategyDecision> second = Feed(strategy, candles);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AFastPeriodThatIsNotShorterThanTheSlowOneIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new EmaCrossStrategy(Fast with { FastPeriod = 12, SlowPeriod = 12 }));
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(5, 1)]
    public void NonsensicalPeriodsAreRejected(int fastPeriod, int slowPeriod)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new EmaCrossStrategy(Fast with { FastPeriod = fastPeriod, SlowPeriod = slowPeriod }));
    }
}
