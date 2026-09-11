namespace SolastaBot.Core.Indicators;

/// <summary>
/// Wilder's smoothing in its averaged form: seed with the simple average of the first
/// <c>period</c> samples, then <c>value = value + (sample - value) / period</c>.
/// </summary>
/// <remarks>
/// Wilder's original formulation keeps a running sum rather than an average. The two differ only by
/// a constant factor of <c>period</c>, which cancels in every ratio we take (the directional
/// indicators divide one smoothed series by another), so one implementation serves both uses.
/// </remarks>
internal sealed class WilderSmoother(int period)
{
    private decimal seedSum;
    private int seedCount;

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

        Value += (sample - Value) / period;
    }
}
