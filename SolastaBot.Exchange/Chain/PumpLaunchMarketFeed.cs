using System.Globalization;
using System.Net;
using System.Text.Json;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Exchange.Chain;

public sealed class PumpLaunchMarketFeed(HttpClient httpClient, TimeProvider timeProvider, decimal requestsPerSecond) : ILaunchMarketFeed, IDisposable
{
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly decimal baseRate = ValidateRate(requestsPerSecond);
    private decimal currentRate = ValidateRate(requestsPerSecond);
    private DateTimeOffset nextRequest = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<LaunchCandidate>> ReadLaunchesAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await RequestAsync("coins?offset=0&limit=50&sort=created_timestamp&order=DESC&includeNsfw=true", cancellationToken);
        decimal now = Now();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Launch feed did not return an array.");
        }

        List<LaunchCandidate> launches = [];
        foreach (JsonElement coin in document.RootElement.EnumerateArray())
        {
            if (coin.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Launch feed contains an invalid item.");
            }

            string? mint = Text(coin, "mint");
            if (string.IsNullOrWhiteSpace(mint))
            {
                continue;
            }

            decimal? decimals = Number(coin, "quote_decimals");
            int? quoteDecimals = decimals is >= 0m and <= 18m && decimals == decimal.Truncate(decimals.Value) ? (int)decimals.Value : null;
            launches.Add(new(mint, (Number(coin, "created_timestamp") ?? 0m) / 1000m, Text(coin, "quote_mint"), quoteDecimals, Text(coin, "protocol"),
                !string.IsNullOrWhiteSpace(Text(coin, "telegram")), !string.IsNullOrWhiteSpace(Text(coin, "twitter")), Curve(coin, mint, now)));
        }

        return [.. launches.OrderBy(launch => launch.CreatedAt).ThenBy(launch => launch.Mint, StringComparer.Ordinal)];
    }

    public async Task<CurveObservation> ReadCurveAsync(string mint, CancellationToken cancellationToken)
    {
        using JsonDocument document = await RequestAsync($"coins/{Uri.EscapeDataString(mint)}", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object || Text(document.RootElement, "mint") != mint)
        {
            throw new InvalidDataException("Curve response did not identify the requested mint.");
        }

        return Curve(document.RootElement, mint, Now());
    }

    public void Dispose()
    {
        requestGate.Dispose();
    }

    private async Task<JsonDocument> RequestAsync(string relativePath, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan wait = nextRequest - timeProvider.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, timeProvider, cancellationToken);
            }

            nextRequest = timeProvider.GetUtcNow().AddSeconds((double)(1m / currentRate));
            using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri("https://frontend-api-v3.pump.fun/" + relativePath));
            request.Headers.UserAgent.ParseAdd("SolastaBot/1.0");
            request.Headers.Accept.ParseAdd("application/json");
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestTimeout.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            {
                currentRate = Math.Max(0.1m, currentRate / 2m);
                TimeSpan retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAt)
                {
                    retry = retryAt - timeProvider.GetUtcNow();
                }

                nextRequest = timeProvider.GetUtcNow() + (retry > TimeSpan.FromSeconds(30) ? retry : TimeSpan.FromSeconds(30));
            }

            response.EnsureSuccessStatusCode();
            await using Stream body = await response.Content.ReadAsStreamAsync(requestTimeout.Token);
            JsonDocument result = await JsonDocument.ParseAsync(body, cancellationToken: requestTimeout.Token);
            currentRate = Math.Min(baseRate, currentRate * 1.05m);
            return result;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private decimal Now() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000m;

    private static CurveObservation Curve(JsonElement source, string mint, decimal now)
    {
        bool? complete = source.TryGetProperty("complete", out JsonElement flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False ? flag.GetBoolean() : null;
        return new(mint, now, complete, Reserve(source, "virtual_sol_reserves"), Reserve(source, "virtual_token_reserves"),
            Reserve(source, "real_sol_reserves"), Reserve(source, "real_token_reserves"), null);
    }

    private static decimal? Reserve(JsonElement source, string name)
    {
        decimal? value = Number(source, name);
        return value is >= 0m and <= ulong.MaxValue && value == decimal.Truncate(value.Value) ? value : null;
    }

    private static decimal? Number(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) ? number : null;
    }

    private static string? Text(JsonElement source, string name) => source.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static decimal ValidateRate(decimal rate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1m);
        return rate;
    }
}
