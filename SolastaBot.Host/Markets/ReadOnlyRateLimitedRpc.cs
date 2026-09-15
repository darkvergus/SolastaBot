using System.Text.Json;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Host.Markets;

public sealed class ReadOnlyRateLimitedRpc(ISolanaRpc inner) : ISolanaRpc, IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private DateTimeOffset next;

    public async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        if (method is "sendTransaction" or "requestAirdrop")
        {
            throw new InvalidOperationException("Paper service cannot send transactions.");
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan delay = next - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            next = DateTimeOffset.UtcNow.AddMilliseconds(500);
            try { return await inner.CallAsync(method, parameters, cancellationToken); }
            catch { next = DateTimeOffset.UtcNow.AddSeconds(15); throw; }
        }
        finally { gate.Release(); }
    }

    public Task<JsonElement> SendAsync(string transaction, ulong minimumSlot, Func<bool> authorized, CancellationToken cancellationToken) => throw new InvalidOperationException("Paper service cannot send transactions.");
    public void Dispose() => gate.Dispose();
}
