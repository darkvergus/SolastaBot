using System.Net.Http.Json;
using System.Text.Json;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class SolanaRpc(HttpClient client, Uri endpoint, bool allowDevnetSend) : ISolanaRpc
{
    public const string DevnetGenesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
    private long sequence;

    public async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        if (method == "sendTransaction")
        {
            throw new InvalidOperationException("Transactions require the guarded SendAsync path.");
        }

        if (method == "requestAirdrop")
        {
            if (!allowDevnetSend)
            {
                throw new InvalidOperationException("Broadcasting is disabled in Simulate mode.");
            }

            JsonElement genesis = await CallAsync("getGenesisHash", [], cancellationToken);
            if (genesis.GetString() != DevnetGenesis)
            {
                throw new InvalidOperationException("Broadcasting is supported only on Solana devnet.");
            }
        }

        return await RequestAsync(method, parameters, cancellationToken);
    }

    public async Task<JsonElement> SendAsync(string transaction, ulong minimumSlot, Func<bool> authorized, CancellationToken cancellationToken)
    {
        if (!allowDevnetSend)
        {
            throw new InvalidOperationException("Broadcasting is disabled in Simulate mode.");
        }

        JsonElement genesis = await CallAsync("getGenesisHash", [], cancellationToken);
        if (genesis.GetString() != DevnetGenesis || !authorized())
        {
            throw new InvalidOperationException("Network or submission control refused the transaction.");
        }

        return await RequestAsync("sendTransaction", [transaction, new { encoding = "base64", skipPreflight = false, preflightCommitment = "confirmed", maxRetries = 0, minContextSlot = minimumSlot }], cancellationToken);
    }

    private async Task<JsonElement> RequestAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, new { jsonrpc = "2.0", id = Interlocked.Increment(ref sequence), method, @params = parameters }, timeout.Token);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        return document.RootElement.TryGetProperty("error", out JsonElement error) ? throw new InvalidOperationException($"Solana {method} failed: {error.GetRawText()}") : document.RootElement.GetProperty("result").Clone();
    }
}
