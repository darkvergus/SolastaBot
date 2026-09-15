using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Markets;

public sealed class SolanaDiscoveryWorker(UnifiedSettings settings, PortfolioStore store, ReadOnlyRateLimitedRpc rpc, Channel<string> discoveries, ILogger<SolanaDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.EnableNetwork)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new(settings.SolanaWebSocketUrl), stoppingToken);
                await SocketMessages.SendAsync(socket, new { jsonrpc = "2.0", id = 1, method = "logsSubscribe", @params = new object[] { new { mentions = new[] { SolanaPrograms.Pump } }, new { commitment = "confirmed" } } }, stoppingToken);
                store.Mutate(state => state.Health["solana-discovery"] = "Connected; reconnect gaps are recorded, REST newest feed supplies fallback coverage");
                await RecoverAsync(stoppingToken);
                while (socket.State == WebSocketState.Open)
                {
                    using JsonDocument message = await SocketMessages.ReadAsync(socket, stoppingToken);
                    if (message.RootElement.TryGetProperty("error", out JsonElement error))
                    {
                        throw new InvalidDataException(error.GetRawText());
                    }

                    if (!message.RootElement.TryGetProperty("params", out JsonElement parameters))
                    {
                        continue;
                    }

                    JsonElement value = parameters.GetProperty("result").GetProperty("value");
                    if (value.GetProperty("err").ValueKind != JsonValueKind.Null)
                    {
                        continue;
                    }

                    bool created = value.GetProperty("logs").EnumerateArray().Any(line => line.GetString()?.Contains("Instruction: Create", StringComparison.Ordinal) == true);
                    if (created)
                    {
                        await DiscoverAsync(value.GetProperty("signature").GetString()!, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or WebSocketException or JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException or OverflowException)
            {
                logger.LogWarning("Solana discovery reconnect: {Reason}", exception.Message);
                store.Observe(new() { Source = "solana-discovery", Market = "", At = Now(), Error = $"Coverage gap: {exception.Message}" });
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        string? cursor = store.Snapshot().Health.GetValueOrDefault("solana-cursor");
        if (cursor is null)
        {
            return;
        }

        JsonElement signatures = await rpc.CallAsync("getSignaturesForAddress", [SolanaPrograms.Pump, new { until = cursor, limit = 1000, commitment = "confirmed" }], cancellationToken);
        if (signatures.GetArrayLength() == 1000)
        {
            store.Observe(new() { Source = "solana-discovery", Market = "", At = Now(), Error = "Recovery exceeded 1000 signatures; launch coverage incomplete" });
        }
        foreach (JsonElement signature in signatures.EnumerateArray().Reverse().TakeLast(100))
        {
            await DiscoverAsync(signature.GetProperty("signature").GetString()!, cancellationToken);
        }
        if (signatures.GetArrayLength() > 100)
        {
            store.Observe(new() { Source = "solana-discovery", Market = "", At = Now(), Error = "Recovery budget capped at 100 transactions; coverage incomplete" });
        }
    }

    private async Task DiscoverAsync(string signature, CancellationToken cancellationToken)
    {
        JsonElement transaction = await rpc.CallAsync("getTransaction", [signature, new { encoding = "jsonParsed", commitment = "confirmed", maxSupportedTransactionVersion = 1 }], cancellationToken);
        if (transaction.ValueKind == JsonValueKind.Null)
        {
            throw new IOException("Discovered transaction not available yet.");
        }

        JsonElement meta = transaction.GetProperty("meta");
        bool created = meta.TryGetProperty("logMessages", out JsonElement logs) && logs.EnumerateArray().Any(line => line.GetString()?.Contains("Instruction: Create", StringComparison.Ordinal) == true);
        if (created && meta.TryGetProperty("postTokenBalances", out JsonElement balances))
        {
            foreach (string mint in balances.EnumerateArray().Select(balance => balance.GetProperty("mint").GetString()!).Where(mint => mint != SolanaPrograms.WrappedSol).Distinct())
            {
                if (!discoveries.Writer.TryWrite(mint))
                {
                    store.Observe(new() { Source = "solana-discovery", Market = mint, At = Now(), Error = "Discovery queue full; launch coverage incomplete" });
                }
            }
        }
        store.Mutate(state => state.Health["solana-cursor"] = signature);
    }

    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
