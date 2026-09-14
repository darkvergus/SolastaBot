namespace SolastaBot.Exchange.Chain.Solana;

public sealed record WalletSnapshot(ulong Lamports, IReadOnlyDictionary<string, ulong> Tokens, ulong Slot);
