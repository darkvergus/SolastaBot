namespace SolastaBot.Chain.Execution;

public sealed record OrderIntent(string Id, string Mint, OrderSide Side, ulong Amount, decimal RequestedAt, string Reason);
