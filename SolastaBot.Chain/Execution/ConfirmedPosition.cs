namespace SolastaBot.Chain.Execution;

public sealed record ConfirmedPosition(string Mint, ulong Tokens, decimal CostSol, decimal EnteredAt, decimal MarkSol, decimal MarkedAt,
    decimal? ExitRequestedAt = null, string? ExitReason = null);
