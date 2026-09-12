using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Indicators;

/// <summary>
/// Wilder's directional movement system: +DI, -DI and the ADX trend-strength reading.
/// </summary>
/// <remarks>
/// The strategy uses ADX only as a floor, to stand aside when price is ranging. A moving-average
/// cross has no edge in chop; it whipsaws, and on perpetuals each whipsaw pays taker fees twice and
/// resets the funding clock.
/// <para>
/// Unlike <see cref="AverageTrueRange"/>, the directional series begins at the second bar, because
/// directional movement is undefined without a prior high and low. This asymmetry is Wilder's, and
/// it is preserved here so the readings match standard charting packages.
/// </para>
/// </remarks>
public sealed class AverageDirectionalIndex
{
    private readonly int period;
    private readonly WilderSmoother trueRangeSmoother;
    private readonly WilderSmoother plusSmoother;
    private readonly WilderSmoother minusSmoother;
    private readonly WilderSmoother indexSmoother;

    private decimal previousHigh;
    private decimal previousLow;
    private decimal previousClose;
    private bool hasPrevious;

    public AverageDirectionalIndex(int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 2);
        this.period = period;
        trueRangeSmoother = new(period);
        plusSmoother = new(period);
        minusSmoother = new(period);
        indexSmoother = new(period);
    }

    public int Period => period;

    /// <summary>Warm-up needs roughly two periods: one for the directional series, one for the index.</summary>
    public bool IsReady => indexSmoother.IsReady;

    public decimal Value => indexSmoother.Value;

    public decimal PlusDirectional { get; private set; }

    public decimal MinusDirectional { get; private set; }

    public void Add(in Candle candle)
    {
        if (!hasPrevious)
        {
            Remember(candle);
            return;
        }

        decimal upMove = candle.High - previousHigh;
        decimal downMove = previousLow - candle.Low;

        decimal plus = upMove > downMove && upMove > 0m ? upMove : 0m;
        decimal minus = downMove > upMove && downMove > 0m ? downMove : 0m;

        trueRangeSmoother.Add(TrueRange.Of(candle, previousClose));
        plusSmoother.Add(plus);
        minusSmoother.Add(minus);
        Remember(candle);

        if (!trueRangeSmoother.IsReady || trueRangeSmoother.Value == 0m)
        {
            return;
        }

        PlusDirectional = 100m * plusSmoother.Value / trueRangeSmoother.Value;
        MinusDirectional = 100m * minusSmoother.Value / trueRangeSmoother.Value;

        decimal total = PlusDirectional + MinusDirectional;
        decimal directionalIndex = total == 0m ? 0m : 100m * Math.Abs(PlusDirectional - MinusDirectional) / total;

        indexSmoother.Add(directionalIndex);
    }

    private void Remember(in Candle candle)
    {
        previousHigh = candle.High;
        previousLow = candle.Low;
        previousClose = candle.Close;
        hasPrevious = true;
    }
}
