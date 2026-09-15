using System.Text.Json;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Tests.Chain.Execution;

internal sealed class FixtureSolanaRpc : ISolanaRpc
{
    public JsonElement Fixture { get; }
    public Dictionary<string, JsonElement> Accounts { get; } = [];
    public List<string[]> Reads { get; } = [];

    public FixtureSolanaRpc(string file)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Solana", file)));
        Fixture = document.RootElement.Clone();
        JsonElement keys = Fixture.GetProperty("accountKeys");
        JsonElement values = Fixture.GetProperty("accounts").GetProperty("value");
        for (int index = 0; index < keys.GetArrayLength(); index++)
        {
            Accounts[keys[index].GetString()!] = values[index];
        }
    }

    public Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getLatestBlockhash":
                return Task.FromResult(JsonSerializer.SerializeToElement(new { value = new { blockhash = "11111111111111111111111111111111", lastValidBlockHeight = 1000 } }));
            case "getFeeForMessage":
                return Task.FromResult(JsonSerializer.SerializeToElement(new { value = 5000 }));
        }

        if (method != "getMultipleAccounts")
        {
            throw new InvalidOperationException($"Unexpected method {method}");
        }

        string[] addresses = (string[])parameters[0];
        Reads.Add(addresses);
        object?[] values = [.. addresses.Select(address => Accounts.TryGetValue(address, out JsonElement value) ? (object)value : null)];
        return Task.FromResult(JsonSerializer.SerializeToElement(new { context = new { slot = 1234 }, value = values }));
    }
}
