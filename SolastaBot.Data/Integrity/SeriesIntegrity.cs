using System.Globalization;
using System.Text;
using SolastaBot.Core.Domain;

namespace SolastaBot.Data.Integrity;

public sealed record IntegrityReport(
    int Count,
    DateTime? First,
    DateTime? Last,
    int MissingCount,
    IReadOnlyList<DateTime> MissingSample,
    IReadOnlyList<DateTime> Duplicates,
    IReadOnlyList<DateTime> OutOfOrder,
    IReadOnlyList<DateTime> Malformed)
{
    public bool IsClean =>
        MissingCount == 0 && Duplicates.Count == 0 && OutOfOrder.Count == 0 && Malformed.Count == 0;

    public string Describe()
    {
        StringBuilder text = new();
        text.Append(CultureInfo.InvariantCulture, $"{Count} bars");

        if (First is not null && Last is not null)
        {
            text.Append(CultureInfo.InvariantCulture, $" from {First:yyyy-MM-dd HH:mm} to {Last:yyyy-MM-dd HH:mm}");
        }

        if (IsClean)
        {
            return text.Append(", no gaps or duplicates.").ToString();
        }

        text.Append(':');
        Append(text, "missing", MissingCount);
        Append(text, "duplicated", Duplicates.Count);
        Append(text, "out of order", OutOfOrder.Count);
        Append(text, "malformed", Malformed.Count);

        if (MissingSample.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $" First gap at {MissingSample[0]:yyyy-MM-dd HH:mm}.");
        }

        return text.ToString();

        static void Append(StringBuilder text, string label, int count)
        {
            if (count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $" {count} {label};");
            }
        }
    }
}

/// <summary>
/// Checks a downloaded series for the flaws that quietly invalidate a backtest.
/// </summary>
/// <remarks>
/// A missing hour is not a visible error. The series still loads, the strategy still runs and the
/// result still looks like a number. What actually happened is that a gap became an artificial price
/// jump, which a trend strategy reads as a signal and a stop reads as a gap-through. Checking the
/// cadence is the only way to know the input is what it claims to be, so this runs as a test over
/// real downloaded data rather than as an optional diagnostic.
/// </remarks>
public static class SeriesIntegrity
{
    private const int SampleLimit = 50;

    public static IntegrityReport Inspect(IReadOnlyList<Candle> candles, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        if (candles.Count == 0)
        {
            return new IntegrityReport(0, null, null, 0, [], [], [], []);
        }

        List<DateTime> missingSample = [];
        List<DateTime> duplicates = [];
        List<DateTime> outOfOrder = [];
        List<DateTime> malformed = [];
        int missingCount = 0;

        DateTime previous = candles[0].OpenTime;
        if (!candles[0].IsWellFormed)
        {
            malformed.Add(previous);
        }

        for (int index = 1; index < candles.Count; index++)
        {
            Candle candle = candles[index];
            DateTime current = candle.OpenTime;

            if (!candle.IsWellFormed)
            {
                malformed.Add(current);
            }

            if (current == previous)
            {
                duplicates.Add(current);
                continue;
            }

            if (current < previous)
            {
                outOfOrder.Add(current);
                previous = current;
                continue;
            }

            DateTime expected = previous + interval;
            if (current > expected)
            {
                // Counted arithmetically so that a multi-year hole does not enumerate millions of slots.
                long absent = (current - expected).Ticks / interval.Ticks;
                missingCount += (int)Math.Min(absent, int.MaxValue);

                for (DateTime slot = expected; slot < current && missingSample.Count < SampleLimit; slot += interval)
                {
                    missingSample.Add(slot);
                }
            }

            previous = current;
        }

        return new IntegrityReport(
            Count: candles.Count,
            First: candles[0].OpenTime,
            Last: candles[^1].OpenTime,
            MissingCount: missingCount,
            MissingSample: missingSample,
            Duplicates: duplicates,
            OutOfOrder: outOfOrder,
            Malformed: malformed);
    }
}
