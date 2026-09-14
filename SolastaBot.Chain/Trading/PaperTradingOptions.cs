using SolastaBot.Chain.Replay;

namespace SolastaBot.Chain.Trading;

public sealed record PaperTradingOptions
{
    public string Mode { get; init; } = "Paper";
    public required CapitalAllocation Capital { get; init; }
    public required ReplayOptions Execution { get; init; }
    public int MaxPositions { get; init; } = 3;
    public decimal MaxPositionFraction { get; init; } = 0.05m;
    public decimal DailyLossFraction { get; init; } = 0.05m;
    public decimal MaxLaunchAgeSeconds { get; init; } = 60m;
    public decimal MinimumVirtualSol { get; init; } = 31.04m;
    public decimal MinimumRealSol { get; init; } = 1m;
    public decimal FeedIntervalSeconds { get; init; } = 20m;
    public decimal PositionPollSeconds { get; init; } = 3m;
    public decimal RequestsPerSecond { get; init; } = 0.8m;

    public decimal EntryCostSol => Execution.TradeSizeSol + Execution.TransactionCostSol + Execution.EntrySetupCostSol;

    public void Validate()
    {
        if (!string.Equals(Mode, "Paper", StringComparison.Ordinal))
        {
            throw new ArgumentException("This host supports Paper mode only. No funded-wallet router is installed.");
        }

        ArgumentNullException.ThrowIfNull(Capital);
        ArgumentNullException.ThrowIfNull(Execution);
        Capital.Validate();
        Execution.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPositions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPositions, 100);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPositionFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPositionFraction, 1m);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(DailyLossFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(DailyLossFraction, 1m);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxLaunchAgeSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumVirtualSol);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumRealSol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FeedIntervalSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PositionPollSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RequestsPerSecond);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RequestsPerSecond, 1m);
        if (EntryCostSol > Capital.ChainCapitalSol * MaxPositionFraction)
        {
            throw new ArgumentException("Trade and entry costs exceed the chain allocation's position limit.");
        }
    }
}
