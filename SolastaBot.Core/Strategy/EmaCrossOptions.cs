namespace SolastaBot.Core.Strategy;

/// <summary>Tunable parameters for <see cref="EmaCrossStrategy"/>.</summary>
public sealed record EmaCrossOptions
{
    public int FastPeriod { get; init; } = 21;

    public int SlowPeriod { get; init; } = 55;

    public int AtrPeriod { get; init; } = 14;

    public int TrendPeriod { get; init; } = 14;

    /// <summary>ADX reading required before opening. Below this, price is ranging and a cross whipsaws.</summary>
    public decimal MinimumTrendStrength { get; init; } = 20m;

    /// <summary>Protective stop distance as a multiple of ATR.</summary>
    public decimal StopAtrMultiple { get; init; } = 2.5m;

    public bool AllowShorts { get; init; } = true;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FastPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(SlowPeriod, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(AtrPeriod, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TrendPeriod, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumTrendStrength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StopAtrMultiple, 0m);

        if (FastPeriod >= SlowPeriod)
        {
            throw new ArgumentException(
                $"Fast period {FastPeriod} must be shorter than slow period {SlowPeriod}.", nameof(FastPeriod));
        }
    }
}
