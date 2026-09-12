using System.Text.Json;

namespace SolastaBot.ChainCollector;

public sealed class PumpFunClient(HttpClient httpClient, AdaptiveRateLimiter rateLimiter)
{
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly AdaptiveRateLimiter rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));

    public async Task<PumpRequestResult> GetAsync(string url, CancellationToken cancellationToken, int attempts = 5)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("A URL is required.", nameof(url));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempts);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            await rateLimiter.WaitAsync(cancellationToken);

            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, url);

                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                request.Headers.Accept.ParseAdd("application/json");

                using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                int statusCode = (int)response.StatusCode;

                if (statusCode is 429 or >= 500)
                {
                    rateLimiter.Penalise();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new(null, $"http{statusCode}");
                }

                await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

                using JsonDocument document = await JsonDocument.ParseAsync(responseStream, default, cancellationToken);

                JsonElement data = document.RootElement.Clone();

                rateLimiter.Recover();

                return new(data, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1d + attempt), cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1d + attempt), cancellationToken);
            }
        }

        return new(null, "exhausted");
    }
}