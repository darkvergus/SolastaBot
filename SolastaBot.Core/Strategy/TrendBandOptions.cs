using System;

namespace SolastaBot.Core.Strategy;

/// <summary>Tunable parameters for <see cref="TrendBandStrategy"/>.</summary>
public sealed record TrendBandOptions
{
    public int FastPeriod { get; init; } = 21;

    public int SlowPeriod { get; init; } = 55;

    public int AtrPeriod { get; init; } = 14;

    public int TrendPeriod { get; init; } = 14;

    /// <summary>
    /// How far past the slow average, in basis points of it, the fast average must sit before a
    /// position opens.
    /// </summary>
    /// <remarks>
    /// Exits happen at the plain cross, so this width is also the dead zone the strategy sits flat
    /// in. That asymmetry is the whole point of the parameter: a cross that barely happens closes
    /// the position but does not open the opposite one, which is what stops a marginal wobble from
    /// costing two taker fees.
    /// </remarks>
    public decimal EntryBandBasisPoints { get; init; } = 50m;

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
        ArgumentOutOfRangeException.ThrowIfNegative(EntryBandBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(EntryBandBasisPoints, 10_000m);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumTrendStrength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StopAtrMultiple, 0m);

        if (FastPeriod >= SlowPeriod)
        {
            throw new ArgumentException($"Fast period {FastPeriod} must be shorter than slow period {SlowPeriod}.", nameof(FastPeriod));
        }
    }
}
