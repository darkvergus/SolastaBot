using System;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Indicators;

namespace SolastaBot.Core.Strategy;

/// <summary>
/// Trend following on a fast/slow exponential moving average cross, gated by ADX and stopped by a
/// multiple of ATR.
/// </summary>
/// <remarks>
/// The asymmetry between entry and exit is deliberate. Entry additionally requires ADX above a
/// floor, because opening into a range is what turns this family of strategy into a fee pump. Exit
/// does not consult ADX: once positioned, only the cross itself or the stop takes us out, so a
/// temporary dip in trend strength during a genuine trend does not churn the position.
/// </remarks>
public sealed class EmaCrossStrategy : BarSequencedStrategy
{
    private readonly EmaCrossOptions options;
    private ExponentialMovingAverage fast;
    private ExponentialMovingAverage slow;
    private AverageTrueRange atr;
    private AverageDirectionalIndex adx;

    public EmaCrossStrategy(EmaCrossOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        fast = new(options.FastPeriod);
        slow = new(options.SlowPeriod);
        atr = new(options.AtrPeriod);
        adx = new(options.TrendPeriod);
    }

    public override string Name => "ema-cross";

    /// <summary>
    /// The slow EMA and the ADX both need warming; ADX costs roughly two of its periods because its
    /// directional series only begins on the second bar and the index smooths those readings again.
    /// </summary>
    public override int WarmupBars => Math.Max(Math.Max(options.SlowPeriod, options.AtrPeriod + 1), options.TrendPeriod * 2 + 1);

    protected override void OnReset()
    {
        fast = new(options.FastPeriod);
        slow = new(options.SlowPeriod);
        atr = new(options.AtrPeriod);
        adx = new(options.TrendPeriod);
    }

    protected override StrategyDecision OnCandle(in MarketSnapshot snapshot)
    {
        Candle candle = snapshot.Candle;
        fast.Add(candle.Close);
        slow.Add(candle.Close);
        atr.Add(candle);
        adx.Add(candle);

        if (!fast.IsReady || !slow.IsReady || !atr.IsReady || !adx.IsReady || atr.Value <= 0m)
        {
            return StrategyDecision.Flat(DecisionReason.Warmup);
        }

        decimal stopDistance = atr.Value * options.StopAtrMultiple;
        PositionSide trend = ResolveTrend();
        PositionSide held = snapshot.Position.Side;

        if (held == trend)
        {
            return new(trend, stopDistance, DecisionReason.Hold);
        }

        if (held != PositionSide.Flat)
        {
            DecisionReason reason = trend == PositionSide.Flat ? DecisionReason.ExitOnCross : DecisionReason.ReverseOnCross;
            return new(trend, stopDistance, reason);
        }

        if (trend == PositionSide.Flat)
        {
            return StrategyDecision.Flat(DecisionReason.Hold);
        }

        if (adx.Value < options.MinimumTrendStrength)
        {
            return StrategyDecision.Flat(DecisionReason.TrendTooWeak);
        }

        return new(trend, stopDistance, trend == PositionSide.Long ? DecisionReason.EnterLong : DecisionReason.EnterShort);
    }

    private PositionSide ResolveTrend()
    {
        if (fast.Value > slow.Value)
        {
            return PositionSide.Long;
        }

        return options.AllowShorts ? PositionSide.Short : PositionSide.Flat;
    }
}
