using System;

namespace SolastaBot.Core.Backtest;

public sealed record BacktestOptions
{
    public decimal StartingBalance { get; init; } = 10_000m;

    public FeeSchedule Fees { get; init; } = FeeSchedule.BinanceUsdFutures;

    public ISlippageModel Slippage { get; init; } = BasisPointSlippage.Medium;

    /// <summary>
    /// Whether funding settlements are charged. Leave this on. It exists only so a test can isolate
    /// funding's contribution, never as a way to make results look better.
    /// </summary>
    public bool ApplyFunding { get; init; } = true;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StartingBalance, 0m);
        ArgumentNullException.ThrowIfNull(Fees);
        ArgumentNullException.ThrowIfNull(Slippage);
    }
}
