using SolastaBot.Chain.Execution;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Tests.Chain.Execution;

internal sealed class StubConnectedRouter : IConnectedOrderRouter
{
    public int Preparations { get; private set; }
    public List<string> Submissions { get; } = [];
    public WalletSnapshot Wallet { get; set; } = new(1_000_000_000, new Dictionary<string, ulong>(), 50);
    public ExecutionReceipt? Receipt { get; set; }
    public bool Expired { get; set; }
    public bool ThrowOnSubmit { get; set; }
    public bool ThrowOnSimulation { get; set; }
    public Action? AfterPrepare { get; set; }

    public Task<ExecutionOrder> PrepareAsync(OrderIntent intent, CancellationToken cancellationToken)
    {
        Preparations++;
        AfterPrepare?.Invoke();
        return Task.FromResult(new ExecutionOrder(intent, OrderStatus.Signed, $"signed-{intent.Id}", $"signature-{intent.Id}", 100,
            checked(10_000_000 + (intent.Side == OrderSide.Buy ? intent.Amount : 0)), 10, "Pump", 50));
    }

    public Task<string> SimulateAsync(ExecutionOrder order, CancellationToken cancellationToken) => ThrowOnSimulation ? throw new InvalidOperationException("Simulation failure") : Task.FromResult("Simulated");

    public Task SubmitAsync(ExecutionOrder order, Func<bool> authorized, CancellationToken cancellationToken)
    {
        if (!authorized()) throw new InvalidOperationException("Control refused submission.");
        Submissions.Add(order.Transaction);
        return ThrowOnSubmit ? throw new HttpRequestException("Timeout after node accepted transaction") : Task.CompletedTask;
    }

    public Task<ExecutionReceipt?> ReceiptAsync(ExecutionOrder order, CancellationToken cancellationToken) => Task.FromResult(Receipt);
    public Task<bool> ExpiredAsync(ExecutionOrder order, CancellationToken cancellationToken) => Task.FromResult(Expired);
    public Task<WalletSnapshot> WalletAsync(CancellationToken cancellationToken, ulong minimumSlot = 0) => Task.FromResult(Wallet);
}
