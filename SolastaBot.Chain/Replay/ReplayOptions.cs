namespace SolastaBot.Chain.Replay;

public sealed record ReplayOptions
{
    public decimal TradeSizeSol { get; init; } = 0.01m;
    public required decimal FeeBasisPoints { get; init; }
    public required decimal SlippageBasisPoints { get; init; }
    public required decimal TransactionCostSol { get; init; }
    public required decimal EntrySetupCostSol { get; init; }
    public decimal EntryDelaySeconds { get; init; } = 5m;
    public decimal ExitDelaySeconds { get; init; } = 1m;
    public decimal HoldSeconds { get; init; } = 300m;
    public decimal MaxObservationGapSeconds { get; init; } = 30m;
    public decimal TakeProfitFraction { get; init; } = 1m;
    public decimal StopLossFraction { get; init; } = 0.5m;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TradeSizeSol);
        ArgumentOutOfRangeException.ThrowIfNegative(FeeBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(FeeBasisPoints, 10_000m);
        ArgumentOutOfRangeException.ThrowIfNegative(SlippageBasisPoints);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(SlippageBasisPoints, 10_000m);
        ArgumentOutOfRangeException.ThrowIfNegative(TransactionCostSol);
        ArgumentOutOfRangeException.ThrowIfNegative(EntrySetupCostSol);
        ArgumentOutOfRangeException.ThrowIfNegative(EntryDelaySeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(ExitDelaySeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HoldSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObservationGapSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TakeProfitFraction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StopLossFraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(StopLossFraction, 1m);
        if (TradeSizeSol * CurvePricing.LamportsPerSol != decimal.Truncate(TradeSizeSol * CurvePricing.LamportsPerSol))
        {
            throw new ArgumentException("Trade size must be a whole number of lamports.");
        }
    }
}
