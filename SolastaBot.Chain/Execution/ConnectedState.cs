namespace SolastaBot.Chain.Execution;

public sealed record ConnectedState
{
    public long Revision { get; init; }
    public required string Identity { get; init; }
    public ulong WalletLamports { get; init; }
    public bool HasReconciled { get; init; }
    public long NetWalletChangeLamports { get; init; }
    public decimal Day { get; init; } = -1m;
    public decimal DayOpeningEquitySol { get; init; }
    public bool DailyHalt { get; init; }
    public string? ReconciliationError { get; init; }
    public string? ExecutionFault { get; init; }
    public IReadOnlyList<ExecutionOrder> Orders { get; init; } = [];
    public IReadOnlyList<ConfirmedPosition> Positions { get; init; } = [];
    public IReadOnlyList<string> SeenMints { get; init; } = [];
    public ulong ReservedLamports => Orders.Where(order => order.Pending).Aggregate(0UL, (total, order) => checked(total + order.ReservedLamports));
    public decimal EquitySol(decimal allocationSol) => allocationSol + NetWalletChangeLamports / 1_000_000_000m + Positions.Sum(position => position.MarkSol);
}
