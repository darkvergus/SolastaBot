using FacioQuo.Stock.Indicators;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Indicators;
using SolastaBot.Tests.Support;

namespace SolastaBot.Tests.Indicators;

/// <summary>
/// Cross-checks the hand-written incremental indicators against an independent implementation.
/// </summary>
/// <remarks>
/// SolastaBot computes indicators itself, in decimal and incrementally, so that Core stays free of
/// dependencies and so the live loop and the backtester share one warm-up path. That freedom is only
/// safe if the arithmetic is right, which is what these tests establish. The reference library runs
/// in double, so parity is asserted to a relative tolerance rather than exactly.
/// </remarks>
public sealed class ReferenceParityTests
{
    private const double Tolerance = 1e-9;

    private static Bar[] ToBars(IReadOnlyList<Candle> candles)
    {
        Bar[] bars = new Bar[candles.Count];
        for (int index = 0; index < candles.Count; index++)
        {
            Candle candle = candles[index];
            bars[index] = new(candle.OpenTime, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume);
        }

        return bars;
    }

    [Theory, InlineData(9), InlineData(21), InlineData(55)]
    public void ExponentialMovingAverageMatchesTheReferenceImplementation(int period)
    {
        Candle[] candles = CandleFactory.RandomWalk(600, seed: 17);
        IReadOnlyList<EmaResult> expected = ToBars(candles).ToEma(period);
        ExponentialMovingAverage actual = new(period);
        int compared = 0;

        for (int index = 0; index < candles.Length; index++)
        {
            actual.Add(candles[index].Close);
            double? reference = expected[index].Ema;

            Assert.Equal(reference is not null, actual.IsReady);
            if (reference is null)
            {
                continue;
            }

            Assert.Equal(reference.Value, (double)actual.Value, Tolerance * Math.Abs(reference.Value));
            compared++;
        }

        Assert.True(compared > 500, $"Only {compared} bars were compared; the warm-up window looks wrong.");
    }

    [Theory, InlineData(7), InlineData(14), InlineData(21)]
    public void AverageTrueRangeMatchesTheReferenceImplementation(int period)
    {
        Candle[] candles = CandleFactory.RandomWalk(600, seed: 23);
        IReadOnlyList<AtrResult> expected = ToBars(candles).ToAtr(period);
        AverageTrueRange actual = new(period);
        int compared = 0;

        for (int index = 0; index < candles.Length; index++)
        {
            actual.Add(candles[index]);
            double? reference = expected[index].Atr;

            Assert.Equal(reference is not null, actual.IsReady);
            if (reference is null)
            {
                continue;
            }

            Assert.Equal(reference.Value, (double)actual.Value, Tolerance * Math.Abs(reference.Value));
            compared++;
        }

        Assert.True(compared > 500, $"Only {compared} bars were compared; the warm-up window looks wrong.");
    }

    [Theory, InlineData(14), InlineData(20)]
    public void AverageDirectionalIndexMatchesTheReferenceImplementation(int period)
    {
        Candle[] candles = CandleFactory.RandomWalk(600, seed: 41);
        IReadOnlyList<AdxResult> expected = ToBars(candles).ToAdx(period);
        AverageDirectionalIndex actual = new(period);
        int compared = 0;

        for (int index = 0; index < candles.Length; index++)
        {
            actual.Add(candles[index]);
            double? reference = expected[index].Adx;

            if (reference is null)
            {
                continue;
            }

            Assert.True(actual.IsReady, $"Reference had a reading at bar {index} but the incremental one did not.");
            Assert.Equal(reference.Value, (double)actual.Value, 1e-6 * Math.Max(1d, Math.Abs(reference.Value)));

            double? plus = expected[index].Pdi;
            double? minus = expected[index].Mdi;
            if (plus is not null && minus is not null)
            {
                Assert.Equal(plus.Value, (double)actual.PlusDirectional, 1e-6 * Math.Max(1d, plus.Value));
                Assert.Equal(minus.Value, (double)actual.MinusDirectional, 1e-6 * Math.Max(1d, minus.Value));
            }

            compared++;
        }

        Assert.True(compared > 500, $"Only {compared} bars were compared; the warm-up window looks wrong.");
    }
}
