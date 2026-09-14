namespace SolastaBot.Exchange.Chain.Solana;

public sealed record SolanaAccount(string Address, string Owner, ulong Lamports, bool Executable, byte[] Data);
