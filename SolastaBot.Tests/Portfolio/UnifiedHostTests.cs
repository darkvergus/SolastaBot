using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using SolastaBot.Host.Dashboard;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Tests.Portfolio;

public sealed class UnifiedHostTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "solasta-host-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DashboardControlsRequireAntiforgeryPersistAndReleaseTheJournalOnShutdown()
    {
        Directory.CreateDirectory(directory);
        UnifiedSettings settings = new() { EnableNetwork = false, ListenUrl = Address() };
        string settingsPath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings), TestContext.Current.CancellationToken);
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(20));
        Task<int> host = UnifiedApplication.RunAsync(["--settings", settingsPath, "--state", Path.Combine(directory, "state")], shutdown.Token);
        using HttpClientHandler handler = new();
        handler.CookieContainer = new();
        using HttpClient client = new(handler);
        client.BaseAddress = new(settings.ListenUrl);
        client.Timeout = TimeSpan.FromSeconds(2);

        try
        {
            await WaitForHostAsync(client, host, shutdown.Token);
            string page = await client.GetStringAsync("/", shutdown.Token);
            Assert.Contains("solana-continuation", page);
            Assert.Contains("binance-trend", page);
            Assert.Contains(".accounts", await client.GetStringAsync("/dashboard.css", shutdown.Token));
            Match match = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
            Assert.True(match.Success);
            using HttpResponseMessage refused = await client.PostAsync("/?handler=Control", new FormUrlEncodedContent(new Dictionary<string, string> { ["command"] = "halt" }), shutdown.Token);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            using HttpResponseMessage accepted = await client.PostAsync("/?handler=Control", new FormUrlEncodedContent(new Dictionary<string, string> { ["command"] = "pause", ["strategy"] = "launch", ["__RequestVerificationToken"] = match.Groups[1].Value }), shutdown.Token);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Contains("Paused", await accepted.Content.ReadAsStringAsync(shutdown.Token));
        }
        finally { await shutdown.CancelAsync(); await host; }
        using PortfolioStore restored = new(Path.Combine(directory, "state"), settings);
        Assert.True(restored.Snapshot().Strategies["launch"].Paused);
        Assert.False(restored.Snapshot().Strategies["continuation"].Paused);
    }

    [Fact]
    public async Task OptInPublicFeedsRecordBinanceMarksAndSolanaDiscoveries()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOLASTA_UNIFIED_FEED_TEST") == "1", "Opt-in bounded read-only public feed check.");
        Directory.CreateDirectory(directory);
        UnifiedSettings settings = new() { ListenUrl = Address() };
        string settingsPath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings), TestContext.Current.CancellationToken);
        using CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(30));
        Task<int> host = UnifiedApplication.RunAsync(["--settings", settingsPath, "--state", Path.Combine(directory, "state")], shutdown.Token);
        Assert.Equal(0, await host);
        using PortfolioStore restored = new(Path.Combine(directory, "state"), settings);
        List<MarketObservation> observations = [.. restored.ReadObservations(0m, decimal.MaxValue)];
        Assert.Contains(observations, observation => observation is { Source: "binance-mark", Price: > 0m });
        Assert.Contains(observations, observation => observation.Launch is not null);
    }

    private static async Task WaitForHostAsync(HttpClient client, Task<int> host, CancellationToken cancellationToken)
    {
        while (!host.IsCompleted)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync("/health", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"Host exited with {await host} before startup.");
    }

    private static string Address()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
