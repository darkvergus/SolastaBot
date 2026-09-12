using System;

namespace SolastaBot.Core.Risk;

public sealed record RiskOptions
{
    /// <summary>Fraction of equity put at risk between entry and stop on a single trade.</summary>
    public decimal RiskFractionPerTrade { get; init; } = 0.005m;

    /// <summary>Hard ceiling on notional divided by equity, applied regardless of what sizing asks for.</summary>
    public int MaxLeverage { get; init; } = 3;

    /// <summary>Fraction of the day's opening equity that may be lost before trading halts until UTC midnight.</summary>
    public decimal DailyLossLimitFraction { get; init; } = 0.03m;

    /// <summary>Losses in a row that stop trading for the remainder of the UTC day.</summary>
    public int MaxConsecutiveLosses { get; init; } = 4;

    /// <summary>
    /// How far, in ATR, the liquidation price must sit beyond entry. The protective stop should always
    /// be hit long before liquidation; this guarantees room for that to happen.
    /// </summary>
    public decimal LiquidationBufferAtrMultiple { get; init; } = 4m;

    /// <summary>Maintenance margin rate for the position's tier. 0.4% covers the lowest Binance USD-M tier.</summary>
    public decimal MaintenanceMarginRate { get; init; } = 0.004m;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RiskFractionPerTrade, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RiskFractionPerTrade, 0.1m);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLeverage, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DailyLossLimitFraction, 0m);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConsecutiveLosses, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(LiquidationBufferAtrMultiple);
        ArgumentOutOfRangeException.ThrowIfNegative(MaintenanceMarginRate);
    }
}
