using SolastaBot.Core.Domain;

namespace SolastaBot.Tests.Support;

/// <summary>
/// Synthetic price series for tests.
/// </summary>
/// <remarks>
/// The pseudo-random generator here is a hand-rolled linear congruential one rather than
/// <see cref="Random"/>, because the framework's sequence is not contractually stable across
/// runtime versions. Tests that assert exact figures need a series that will still be the same
/// series after a .NET upgrade.
/// </remarks>
public static class CandleFactory
{
    public static DateTime Origin { get; } = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(1);

    /// <summary>Builds bars from a close series, deriving each open from the previous close.</summary>
    public static Candle[] FromCloses(IReadOnlyList<decimal> closes, decimal wick = 0.002m)
    {
        ArgumentNullException.ThrowIfNull(closes);
        Candle[] candles = new Candle[closes.Count];
        decimal previousClose = closes.Count > 0 ? closes[0] : 0m;

        for (int index = 0; index < closes.Count; index++)
        {
            decimal open = index == 0 ? closes[0] : previousClose;
            decimal close = closes[index];
            decimal high = Math.Max(open, close) * (1m + wick);
            decimal low = Math.Min(open, close) * (1m - wick);
            candles[index] = new(Origin + Interval * index, open, high, low, close, 1m);
            previousClose = close;
        }

        return candles;
    }

    public static Candle[] Trend(int count, decimal start, decimal perBar)
    {
        decimal[] closes = new decimal[count];
        for (int index = 0; index < count; index++)
        {
            closes[index] = start + perBar * index;
        }

        return FromCloses(closes);
    }

    /// <summary>A deterministic random walk. The same seed always yields the same series.</summary>
    public static Candle[] RandomWalk(int count, int seed, decimal start = 30_000m, decimal volatility = 0.01m)
    {
        decimal[] closes = new decimal[count];
        ulong state = (ulong)seed + 0x9E3779B97F4A7C15UL;
        decimal price = start;

        for (int index = 0; index < count; index++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            // Top 20 bits give a stable value in [-1, 1) with no floating point involved.
            decimal unit = (state >> 44) / 524_288m - 1m;
            price *= 1m + unit * volatility;
            price = Math.Round(Math.Max(price, 1m), 2);
            closes[index] = price;
        }

        return FromCloses(closes);
    }

    /// <summary>Eight-hourly funding settlements at a constant rate, as a perpetual venue produces.</summary>
    public static FundingEvent[] Funding(DateTime from, int count, decimal rate)
    {
        FundingEvent[] events = new FundingEvent[count];
        for (int index = 0; index < count; index++)
        {
            events[index] = new(from + TimeSpan.FromHours(8 * index), rate);
        }

        return events;
    }
}
