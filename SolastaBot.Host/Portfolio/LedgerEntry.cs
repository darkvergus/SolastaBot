namespace SolastaBot.Host.Portfolio;

public sealed record LedgerEntry(long Sequence, decimal At, string Strategy, string Kind, string Market, decimal Amount, decimal Quantity, string Reason);
