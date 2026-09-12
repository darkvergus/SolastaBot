using System;

namespace SolastaBot.Core.Backtest;

/// <summary>
/// Exchange commission in basis points. Defaults are the Binance USD-M futures standard tier.
/// </summary>
/// <remarks>
/// Maker and taker are held separately rather than blended because a trend strategy that reverses on
/// a cross pays taker twice per reversal, and blending hides how much of the edge that consumes.
/// </remarks>
public sealed record FeeSchedule(decimal MakerBasisPoints, decimal TakerBasisPoints)
{
    public static FeeSchedule BinanceUsdFutures { get; } = new(2m, 5m);

    public static FeeSchedule Free { get; } = new(0m, 0m);

    public decimal TakerFee(decimal notional) => Math.Abs(notional) * TakerBasisPoints / 10_000m;

    public decimal MakerFee(decimal notional) => Math.Abs(notional) * MakerBasisPoints / 10_000m;
}
