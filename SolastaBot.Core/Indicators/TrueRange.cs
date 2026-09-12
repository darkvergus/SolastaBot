using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Indicators;

public static class TrueRange
{
    /// <summary>
    /// The greater of the bar's own range and its gap from the previous close, which is what makes
    /// true range account for gaps across a settlement or a halt.
    /// </summary>
    /// <remarks>
    /// There is deliberately no overload for the first bar. True range is undefined without a
    /// previous close, so the first bar of a series contributes nothing rather than contributing a
    /// high-to-low range that would bias the average low.
    /// </remarks>
    public static decimal Of(in Candle candle, decimal previousClose)
    {
        decimal range = candle.High - candle.Low;
        decimal upGap = Math.Abs(candle.High - previousClose);
        decimal downGap = Math.Abs(candle.Low - previousClose);
        return Math.Max(range, Math.Max(upGap, downGap));
    }
}
