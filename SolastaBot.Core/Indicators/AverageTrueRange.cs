using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Indicators;

/// <summary>
/// Wilder's average true range.
/// </summary>
/// <remarks>
/// The first bar is consumed for its close but contributes no true range, so the average seeds from
/// the mean of the next <c>period</c> readings and the indicator is ready on bar <c>period</c>
/// counting from zero. This matches the Stock.Indicators reference implementation, which the parity
/// tests check against.
/// </remarks>
public sealed class AverageTrueRange
{
    private readonly WilderSmoother smoother;
    private decimal previousClose;
    private bool hasPrevious;

    public AverageTrueRange(int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        Period = period;
        smoother = new(period);
    }

    public int Period { get; }

    public bool IsReady => smoother.IsReady;

    public decimal Value => smoother.Value;

    public void Add(in Candle candle)
    {
        if (hasPrevious)
        {
            smoother.Add(TrueRange.Of(candle, previousClose));
        }

        previousClose = candle.Close;
        hasPrevious = true;
    }
}
