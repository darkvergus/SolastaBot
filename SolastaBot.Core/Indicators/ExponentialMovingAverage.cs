using System;
using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Indicators;

/// <summary>
/// Incremental exponential moving average, seeded with the simple average of the first
/// <c>period</c> samples.
/// </summary>
/// <remarks>
/// Incremental rather than window-recomputing so that the live loop and the backtester share one
/// warm-up path. A recompute-the-window implementation quietly disagrees with a streaming one at the
/// boundary, and that disagreement is exactly what makes a backtest stop predicting live behaviour.
/// </remarks>
public sealed class ExponentialMovingAverage
{
    private readonly int period;
    private readonly decimal multiplier;
    private decimal seedSum;
    private int seedCount;

    public ExponentialMovingAverage(int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        this.period = period;
        multiplier = 2m / (period + 1);
    }

    public int Period => period;

    public bool IsReady { get; private set; }

    public decimal Value { get; private set; }

    public void Add(decimal sample)
    {
        if (!IsReady)
        {
            seedSum += sample;
            seedCount++;
            if (seedCount < period)
            {
                return;
            }

            Value = seedSum / period;
            IsReady = true;
            return;
        }

        Value += (sample - Value) * multiplier;
    }

    public void Add(in Candle candle) => Add(candle.Close);
}
