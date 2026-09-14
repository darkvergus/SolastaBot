namespace SolastaBot.Chain.Execution;

public sealed record ExecutionReceipt(bool Failed, long WalletChangeLamports, long TokenChange, ulong FeeLamports, ulong Slot);
