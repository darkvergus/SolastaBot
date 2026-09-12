using System;

namespace SolastaBot.Core.Strategy;

/// <summary>
/// Base class enforcing the one-call-per-closed-bar contract.
/// </summary>
/// <remarks>
/// Strategies keep incremental indicator state, so feeding the same bar twice, or feeding bars out
/// of order, silently corrupts every reading that follows and produces a backtest that can never be
/// reproduced live. That is a class of bug which is almost impossible to spot in the results, so it
/// is turned into an exception at the boundary instead.
/// </remarks>
public abstract class BarSequencedStrategy : IStrategy
{
    private DateTime lastOpenTime = DateTime.MinValue;

    public abstract string Name { get; }

    public abstract int WarmupBars { get; }

    public void Reset()
    {
        lastOpenTime = DateTime.MinValue;
        OnReset();
    }

    public StrategyDecision Evaluate(in MarketSnapshot snapshot)
    {
        if (snapshot.Candle.OpenTime <= lastOpenTime)
        {
            throw new InvalidOperationException($"{Name} received a bar opening at {snapshot.Candle.OpenTime:O} after one opening at {lastOpenTime:O}. Bars must arrive once each, in ascending order.");
        }

        lastOpenTime = snapshot.Candle.OpenTime;
        return OnCandle(snapshot);
    }

    protected abstract void OnReset();

    protected abstract StrategyDecision OnCandle(in MarketSnapshot snapshot);
}
