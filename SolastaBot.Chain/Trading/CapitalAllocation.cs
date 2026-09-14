namespace SolastaBot.Chain.Trading;

public sealed record CapitalAllocation
{
    public decimal TotalPaperCapitalSol { get; init; } = 10m;
    public decimal ChainFraction { get; init; } = 0.2m;
    public decimal PerpFraction { get; init; } = 0.5m;
    public decimal ChainCapitalSol => TotalPaperCapitalSol * ChainFraction;
    public decimal PerpReserveSol => TotalPaperCapitalSol * PerpFraction;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TotalPaperCapitalSol);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChainFraction);
        ArgumentOutOfRangeException.ThrowIfNegative(PerpFraction);
        if (ChainFraction + PerpFraction > 1m)
        {
            throw new ArgumentException("Chain and perp allocations cannot exceed total capital.");
        }
    }
}
