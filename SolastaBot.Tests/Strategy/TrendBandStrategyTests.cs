using System;
using System.Collections.Generic;
using System.Linq;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;
using Xunit;

namespace SolastaBot.Tests.Strategy;

public sealed class TrendBandStrategyTests
{
    private static Instrument Instrument { get; } = Instrument.BtcUsdtPerpetual;

    private static TrendBandOptions Fast { get; } = new()
    {
        FastPeriod = 5,
        SlowPeriod = 12,
        AtrPeriod = 5,
        TrendPeriod = 5,
        EntryBandBasisPoints = 50m,
        MinimumTrendStrength = 20m,
        StopAtrMultiple = 2m
    };

    /// <summary>
    /// Replays a series the way the engine does, carrying the position the strategy asked for into
    /// the next bar so the hold and exit branches are actually reached.
    /// </summary>
    private static List<StrategyDecision> Feed(IStrategy strategy, IReadOnlyList<Candle> candles)
    {
        List<StrategyDecision> decisions = [];
        PositionState position = PositionState.Flat;

        foreach (Candle candle in candles)
        {
            StrategyDecision decision = strategy.Evaluate(new(Instrument, candle, position));
            decisions.Add(decision);

            position = decision.TargetSide == PositionSide.Flat ? PositionState.Flat : new()
            {
                Side = decision.TargetSide,
                Quantity = 1m,
                EntryPrice = candle.Close,
                StopPrice = candle.Close - decision.StopDistance
            };
        }

        return decisions;
    }

    /// <summary>Rises for <paramref name="up"/> bars, then falls for <paramref name="down"/>.</summary>
    private static Candle[] UpThenDown(int up, int down, decimal start, decimal step)
    {
        List<decimal> closes = [];
        decimal price = start;

        for (int index = 0; index < up; index++)
        {
            closes.Add(price);
            price += step;
        }

        for (int index = 0; index < down; index++)
        {
            closes.Add(price);
            price -= step;
        }

        return CandleFactory.FromCloses(closes);
    }

    [Fact]
    public void FeedingTheSameBarTwiceThrowsRatherThanCorruptingTheIndicators()
    {
        TrendBandStrategy strategy = new(Fast);
        Candle candle = CandleFactory.Trend(1, 100m, 1m)[0];
        MarketSnapshot snapshot = new(Instrument, candle, PositionState.Flat);

        strategy.Evaluate(snapshot);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => strategy.Evaluate(snapshot));
        Assert.Contains("ascending order", error.Message);
    }

    [Fact]
    public void NothingIsProposedUntilEveryIndicatorHasWarmedUp()
    {
        TrendBandStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(60, 100m, 1m));

        for (int index = 0; index < strategy.WarmupBars - 1; index++)
        {
            Assert.Equal(DecisionReason.Warmup, decisions[index].Reason);
            Assert.Equal(PositionSide.Flat, decisions[index].TargetSide);
        }
    }

    [Fact]
    public void ASustainedRiseIsTakenLongWithAStopBelow()
    {
        TrendBandStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 100m, 1m));

        StrategyDecision entry = Assert.Single(decisions, decision => decision.Reason == DecisionReason.EnterLong);
        Assert.Equal(PositionSide.Long, entry.TargetSide);
        Assert.True(entry.StopDistance > 0m, "An entry must carry a positive stop distance.");
    }

    [Fact]
    public void ASustainedFallIsTakenShort()
    {
        TrendBandStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 300m, -1m));

        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.EnterShort);
    }

    [Fact]
    public void ShortsAreNeverProposedWhenTheyAreDisallowed()
    {
        TrendBandStrategy strategy = new(Fast with { AllowShorts = false });
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.Trend(120, 300m, -1m));

        Assert.DoesNotContain(decisions, decision => decision.TargetSide == PositionSide.Short);
    }

    /// <summary>
    /// The whole reason this strategy exists. Every bar that changes direction must pass through a
    /// flat target; a long that becomes a short in one decision is the reversal that paid two taker
    /// fees and sank the strategy this one replaces.
    /// </summary>
    [Fact]
    public void ADirectionChangeAlwaysPassesThroughFlatAndNeverReverses()
    {
        TrendBandStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, UpThenDown(90, 90, 100m, 1m));

        for (int index = 1; index < decisions.Count; index++)
        {
            PositionSide previous = decisions[index - 1].TargetSide;
            PositionSide current = decisions[index].TargetSide;

            Assert.False(previous == PositionSide.Long && current == PositionSide.Short, $"Bar {index} reversed long to short without going flat.");
            Assert.False(previous == PositionSide.Short && current == PositionSide.Long, $"Bar {index} reversed short to long without going flat.");
        }

        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.ExitOnCross);
    }

    [Fact]
    public void AnOpenPositionIsClosedWhenTheAveragesCrossBack()
    {
        TrendBandStrategy strategy = new(Fast);
        List<StrategyDecision> decisions = Feed(strategy, UpThenDown(90, 90, 100m, 1m));

        int entry = decisions.FindIndex(decision => decision.Reason == DecisionReason.EnterLong);
        int exit = decisions.FindIndex(entry + 1, decision => decision.Reason == DecisionReason.ExitOnCross);

        Assert.True(entry >= 0, "The rise should have been entered.");
        Assert.True(exit > entry, "The fall should have closed the long.");
        Assert.Equal(PositionSide.Flat, decisions[exit].TargetSide);
    }

    /// <summary>
    /// A cross alone must not open anything: the separation has to clear the band first. Without
    /// that asymmetry there is no dead zone and the strategy is the one it replaces.
    /// </summary>
    [Fact]
    public void AnImpossiblyWideBandExitsButNeverOpensAgain()
    {
        TrendBandStrategy strategy = new(Fast with { EntryBandBasisPoints = 9_000m });
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.RandomWalk(400, seed: 17));

        Assert.DoesNotContain(decisions, decision => decision.Reason is DecisionReason.EnterLong or DecisionReason.EnterShort);
        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.InsideBand);
    }

    [Fact]
    public void AWiderBandNeverTradesMoreOftenThanANarrowerOne()
    {
        int narrow = Feed(new TrendBandStrategy(Fast with { EntryBandBasisPoints = 10m }), CandleFactory.RandomWalk(1_500, seed: 31))
            .Count(decision => decision.Reason is DecisionReason.EnterLong or DecisionReason.EnterShort);

        int wide = Feed(new TrendBandStrategy(Fast with { EntryBandBasisPoints = 300m }), CandleFactory.RandomWalk(1_500, seed: 31))
            .Count(decision => decision.Reason is DecisionReason.EnterLong or DecisionReason.EnterShort);

        Assert.True(wide <= narrow, $"A 300bp band opened {wide} positions where a 10bp band opened {narrow}.");
    }

    [Fact]
    public void AnImpossiblyHighTrendFloorBlocksEveryEntry()
    {
        TrendBandStrategy strategy = new(Fast with { MinimumTrendStrength = 1_000m });
        List<StrategyDecision> decisions = Feed(strategy, CandleFactory.RandomWalk(400, seed: 5));

        Assert.DoesNotContain(decisions, decision => decision.Reason is DecisionReason.EnterLong or DecisionReason.EnterShort);
        Assert.Contains(decisions, decision => decision.Reason == DecisionReason.TrendTooWeak);
    }

    [Fact]
    public void TheBandIsAFractionOfPriceSoItMeansTheSameThingAtAnyLevel()
    {
        Candle[] cheap = CandleFactory.RandomWalk(600, seed: 44, start: 1_000m);
        Candle[] dear = CandleFactory.RandomWalk(600, seed: 44, start: 100_000m);

        List<DecisionReason> fromCheap = Feed(new TrendBandStrategy(Fast), cheap).Select(decision => decision.Reason).ToList();
        List<DecisionReason> fromDear = Feed(new TrendBandStrategy(Fast), dear).Select(decision => decision.Reason).ToList();

        Assert.Equal(fromCheap, fromDear);
    }

    [Fact]
    public void ResetRewindsTheStrategyCompletely()
    {
        TrendBandStrategy strategy = new(Fast);
        Candle[] candles = CandleFactory.RandomWalk(300, seed: 88);

        List<StrategyDecision> first = Feed(strategy, candles);
        strategy.Reset();
        List<StrategyDecision> second = Feed(strategy, candles);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AFastPeriodThatIsNotShorterThanTheSlowOneIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new TrendBandStrategy(Fast with { FastPeriod = 12, SlowPeriod = 12 }));
    }

    [Theory, InlineData(0, 12), InlineData(5, 1)]
    public void NonsensicalPeriodsAreRejected(int fastPeriod, int slowPeriod)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TrendBandStrategy(Fast with { FastPeriod = fastPeriod, SlowPeriod = slowPeriod }));
    }

    [Fact]
    public void ANegativeBandIsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TrendBandStrategy(Fast with { EntryBandBasisPoints = -1m }));
    }
}
