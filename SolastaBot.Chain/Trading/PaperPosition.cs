namespace SolastaBot.Chain.Trading;

public sealed record PaperPosition(string Mint, decimal Tokens, decimal EnteredAt, decimal CostSol, decimal LastObservationAt, decimal MarkSol,
    decimal? ExitRequestedAt = null, string? ExitReason = null, bool Migrated = false);
