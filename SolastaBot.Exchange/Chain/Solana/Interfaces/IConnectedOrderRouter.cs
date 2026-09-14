using SolastaBot.Chain.Execution;

namespace SolastaBot.Exchange.Chain.Solana.Interfaces;

public interface IConnectedOrderRouter
{
    Task<ExecutionOrder> PrepareAsync(OrderIntent intent, CancellationToken cancellationToken);
    Task<string> SimulateAsync(ExecutionOrder order, CancellationToken cancellationToken);
    Task SubmitAsync(ExecutionOrder order, Func<bool> authorized, CancellationToken cancellationToken);
    Task<ExecutionReceipt?> ReceiptAsync(ExecutionOrder order, CancellationToken cancellationToken);
    Task<bool> ExpiredAsync(ExecutionOrder order, CancellationToken cancellationToken);
    Task<WalletSnapshot> WalletAsync(CancellationToken cancellationToken, ulong minimumSlot = 0);
}
