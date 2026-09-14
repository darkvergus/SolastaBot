using System.Text.Json;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Exchange.Chain.Solana;

public static class SolanaAccounts
{
    public static async Task<AccountBatch> ReadAsync(ISolanaRpc rpc, string[] addresses, CancellationToken cancellationToken, ulong minimumSlot = 0)
    {
        JsonElement result = await rpc.CallAsync("getMultipleAccounts", [addresses, new { encoding = "base64", commitment = "confirmed", minContextSlot = minimumSlot }], cancellationToken);
        JsonElement.ArrayEnumerator values = result.GetProperty("value").EnumerateArray();
        List<SolanaAccount?> accounts = [];
        foreach (JsonElement value in values)
        {
            string address = addresses[accounts.Count];
            accounts.Add(value.ValueKind == JsonValueKind.Null ? null : new(address, value.GetProperty("owner").GetString()!, value.GetProperty("lamports").GetUInt64(),
                value.GetProperty("executable").GetBoolean(), Convert.FromBase64String(value.GetProperty("data")[0].GetString()!)));
        }
        return accounts.Count != addresses.Length ? throw new InvalidDataException("RPC returned the wrong account count.") : new(result.GetProperty("context").GetProperty("slot").GetUInt64(), accounts);
    }
}
