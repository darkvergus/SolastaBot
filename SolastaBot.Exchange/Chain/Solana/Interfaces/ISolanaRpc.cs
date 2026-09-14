using System.Text.Json;

namespace SolastaBot.Exchange.Chain.Solana.Interfaces;

public interface ISolanaRpc
{
    Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken);
    Task<JsonElement> SendAsync(string transaction, ulong minimumSlot, Func<bool> authorized, CancellationToken cancellationToken)
        => throw new InvalidOperationException("This RPC transport does not support guarded submission.");
}
