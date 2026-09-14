namespace SolastaBot.Chain.Trading;

public sealed record PendingEntry(string Mint, decimal RequestedAt, decimal ExecuteAfter, decimal ExpiresAt);
