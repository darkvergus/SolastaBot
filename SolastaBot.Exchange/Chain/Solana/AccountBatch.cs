namespace SolastaBot.Exchange.Chain.Solana;

public sealed record AccountBatch(ulong Slot, IReadOnlyList<SolanaAccount?> Accounts);
