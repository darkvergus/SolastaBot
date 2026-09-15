using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using SolastaBot.Core.Domain;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Markets;

public sealed class BinancePaperWorker(UnifiedSettings settings, PortfolioStore store, HttpClient client, ILogger<BinancePaperWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, Instrument> instruments = [];

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
                await LoadInstrumentsAsync(stoppingToken);
                StrategySettings[] strategies = [.. store.Snapshot().Strategies.Values.Select(account => account.Settings).Where(strategy => strategy.Asset == "USDT")];
                if (strategies.Length == 0)
                {
                    return;
                }

                foreach (StrategySettings strategy in strategies.DistinctBy(strategy => (strategy.Symbol, strategy.Interval)))
                {
                    await BackfillAsync(strategy, stoppingToken);
                }

                await FundingAsync(stoppingToken);
                using ClientWebSocket socket = new();
                await socket.ConnectAsync(new(settings.BinanceWebSocketUrl), stoppingToken);
                string[] streams =
                [
                    .. strategies.Select(strategy => $"{strategy.Symbol.ToLowerInvariant()}@kline_{strategy.Interval}")
                        .Concat(strategies.Select(strategy => $"{strategy.Symbol.ToLowerInvariant()}@markPrice@1s")).Distinct()
                ];
                await SocketMessages.SendAsync(socket, new { method = "SUBSCRIBE", @params = streams, id = 1 }, stoppingToken);
                DateTimeOffset nextFunding = DateTimeOffset.UtcNow.AddMinutes(1);
                while (socket.State == WebSocketState.Open)
                {
                    using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using JsonDocument message = await SocketMessages.ReadAsync(socket, timeout.Token);
                    await ApplyMessageAsync(message.RootElement, stoppingToken);
                    if (DateTimeOffset.UtcNow >= nextFunding)
                    {
                        await FundingAsync(stoppingToken);
                        nextFunding = DateTimeOffset.UtcNow.AddMinutes(1);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or WebSocketException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException or OverflowException)
            {
                logger.LogWarning("Binance reconnect: {Reason}", exception.Message);
                store.Observe(new() { Source = "binance", Market = "", At = Now(), Error = $"Coverage gap: {exception.Message}" });
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private async Task LoadInstrumentsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetAsync("/fapi/v1/exchangeInfo", cancellationToken);
        foreach (JsonElement symbol in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            if (symbol.GetProperty("status").GetString() != "TRADING" || symbol.GetProperty("contractType").GetString() != "PERPETUAL" || symbol.GetProperty("quoteAsset").GetString() != "USDT")
            {
                continue;
            }

            JsonElement[] filters = [.. symbol.GetProperty("filters").EnumerateArray()];
            JsonElement price = filters.Single(filter => filter.GetProperty("filterType").GetString() == "PRICE_FILTER");
            JsonElement lot = filters.Single(filter => filter.GetProperty("filterType").GetString() == "LOT_SIZE");
            JsonElement notional = filters.Single(filter => filter.GetProperty("filterType").GetString() == "MIN_NOTIONAL");
            string name = symbol.GetProperty("symbol").GetString()!;
            instruments[name] = new(name, symbol.GetProperty("baseAsset").GetString()!, "USDT", Number(price.GetProperty("tickSize")), Number(lot.GetProperty("stepSize")), Number(lot.GetProperty("minQty")), Number(notional.GetProperty("notional")), 1);
        }
    }

    private async Task BackfillAsync(StrategySettings strategy, CancellationToken cancellationToken)
    {
        if (!instruments.ContainsKey(strategy.Symbol))
        {
            throw new InvalidDataException($"Unsupported Binance instrument {strategy.Symbol}.");
        }

        StrategyAccount account = store.Snapshot().Strategies.Values.First(candidate => candidate.Settings.Symbol == strategy.Symbol && candidate.Settings.Interval == strategy.Interval && candidate.Settings.Asset == "USDT");
        long interval = strategy.Interval == "1h" ? 3_600_000 : 14_400_000;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long start = account.Candles.Count == 0 ? now - interval * 200 : new DateTimeOffset(account.Candles[^1].OpenTime).ToUnixTimeMilliseconds() + interval;
        while (start < now - interval)
        {
            using JsonDocument document = await GetAsync($"/fapi/v1/klines?symbol={strategy.Symbol}&interval={strategy.Interval}&startTime={start}&limit=1000", cancellationToken);
            JsonElement[] bars = [.. document.RootElement.EnumerateArray()];
            if (bars.Length == 0)
            {
                break;
            }

            foreach (JsonElement bar in bars)
            {
                long opened = bar[0].GetInt64();
                long closed = bar[6].GetInt64();
                if (closed >= now)
                {
                    continue;
                }

                Candle candle = new(DateTimeOffset.FromUnixTimeMilliseconds(opened).UtcDateTime, Number(bar[1]), Number(bar[2]), Number(bar[3]), Number(bar[4]), Number(bar[5]));
                store.Observe(new() { Source = "binance-bars", Market = strategy.Symbol, At = (closed + 1) / 1000m, Candle = candle, Interval = strategy.Interval, Instrument = instruments[strategy.Symbol], Warmup = true });
                start = opened + interval;
            }
            if (bars.Length < 1000)
            {
                break;
            }
        }
    }

    private async Task FundingAsync(CancellationToken cancellationToken)
    {
        PortfolioState state = store.Snapshot();
        foreach (string symbol in state.Strategies.Values.Where(account => account.Settings.Asset == "USDT").Select(account => account.Settings.Symbol).Distinct())
        {
            decimal last = state.Strategies.Values.Where(account => account.Settings.Asset == "USDT" && account.Settings.Symbol == symbol).Min(account => account.LastFundingAt);
            long start = last == 0m ? DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds() : (long)(last * 1000m) + 1;
            using JsonDocument document = await GetAsync($"/fapi/v1/fundingRate?symbol={symbol}&startTime={start}&limit=1000", cancellationToken);
            foreach (JsonElement funding in document.RootElement.EnumerateArray())
            {
                store.Observe(new() { Source = "binance-funding", Market = symbol, At = funding.GetProperty("fundingTime").GetInt64() / 1000m, Price = Number(funding.GetProperty("markPrice")), FundingRate = Number(funding.GetProperty("fundingRate")), Instrument = instruments[symbol] });
            }
        }
    }

    private async Task ApplyMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.TryGetProperty("data", out JsonElement combined))
        {
            message = combined;
        }

        if (message.TryGetProperty("error", out JsonElement error))
        {
            throw new InvalidDataException(error.GetRawText());
        }

        if (!message.TryGetProperty("e", out JsonElement kind))
        {
            return;
        }

        string symbol = message.GetProperty("s").GetString()!;
        if (!instruments.TryGetValue(symbol, out Instrument? instrument))
        {
            return;
        }

        decimal at = message.GetProperty("E").GetInt64() / 1000m;
        if (kind.GetString() == "markPriceUpdate")
        {
            store.Observe(new() { Source = "binance-mark", Market = symbol, At = at, ReceivedAt = Now(), Price = Number(message.GetProperty("p")), Instrument = instrument });
        }
        else if (kind.GetString() == "kline")
        {
            JsonElement bar = message.GetProperty("k");
            if (!bar.GetProperty("x").GetBoolean())
            {
                return;
            }

            Candle candle = new(DateTimeOffset.FromUnixTimeMilliseconds(bar.GetProperty("t").GetInt64()).UtcDateTime, Number(bar.GetProperty("o")), Number(bar.GetProperty("h")), Number(bar.GetProperty("l")), Number(bar.GetProperty("c")), Number(bar.GetProperty("v")));
            string interval = bar.GetProperty("i").GetString()!;
            StrategyAccount? account = store.Snapshot().Strategies.Values.FirstOrDefault(account => account.Settings.Asset == "USDT" && account.Settings.Symbol == symbol && account.Settings.Interval == interval);
            if (account is not null && account.Candles.Count > 0 && candle.OpenTime > account.Candles[^1].OpenTime.AddHours(interval == "1h" ? 1 : 4))
            {
                store.Observe(new() { Source = "binance", Market = symbol, At = at, Error = "Closed-bar gap; backfilling before resuming signals" });
                await BackfillAsync(account.Settings, cancellationToken);
            }
            store.Observe(new() { Source = "binance-bars", Market = symbol, At = at, ReceivedAt = Now(), Candle = candle, Interval = interval, Instrument = instrument });
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(settings.BinanceRestUrl.TrimEnd('/') + path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static decimal Number(JsonElement value) => decimal.Parse(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
