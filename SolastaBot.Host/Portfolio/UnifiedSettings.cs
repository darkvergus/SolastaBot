namespace SolastaBot.Host.Portfolio;

public sealed record UnifiedSettings
{
    public string Mode { get; init; } = "Paper";
    public string ListenUrl { get; init; } = "http://127.0.0.1:5080";
    public bool EnableNetwork { get; init; } = true;
    public string SolanaRpcUrl { get; init; } = "https://api.mainnet-beta.solana.com";
    public string SolanaWebSocketUrl { get; init; } = "wss://api.mainnet-beta.solana.com";
    public string BinanceRestUrl { get; init; } = "https://fapi.binance.com";
    public string BinanceWebSocketUrl { get; init; } = "wss://fstream.binance.com/market/stream";
    public int TrackingCapacity { get; init; } = 4;
    public decimal ControlProbability { get; init; } = 0.001m;
    public int ResearchHourUtc { get; init; } = 3;
    public List<StrategySettings> Strategies { get; init; } =
    [
        new() { Id = "launch", Kind = "solana-launch", InitialCapital = 5m },
        new() { Id = "continuation", Kind = "solana-continuation", InitialCapital = 5m, StopFraction = 0.2m, HoldSeconds = 3600 },
        new() { Id = "ema", Kind = "binance-ema", InitialCapital = 5000m, Interval = "1h", FeeBasisPoints = 5m, SlippageBasisPoints = 15m },
        new() { Id = "trend", Kind = "binance-trend", InitialCapital = 5000m, FeeBasisPoints = 5m, SlippageBasisPoints = 15m }
    ];

    public void Validate()
    {
        if (Mode != "Paper")
        {
            throw new ArgumentException("The unified portfolio currently supports Paper only.");
        }

        Uri address = new(ListenUrl);
        if (address.Scheme != "http" || !address.IsLoopback)
        {
            throw new ArgumentException("Dashboard must bind to loopback; use an SSH tunnel.");
        }

        if (TrackingCapacity < 1 || TrackingCapacity > 100 || ControlProbability <= 0m || ControlProbability > 1m || ResearchHourUtc is < 0 or > 23 || Strategies.Count == 0 || Strategies.Count > 20)
        {
            throw new ArgumentException("Invalid collection or research settings.");
        }
        if (Strategies.Select(strategy => strategy.Id).Distinct(StringComparer.Ordinal).Count() != Strategies.Count)
        {
            throw new ArgumentException("Strategy IDs must be unique.");
        }

        foreach (StrategySettings strategy in Strategies)
        {
            strategy.Validate();
        }

        if (new[] { SolanaRpcUrl, BinanceRestUrl }.Any(endpoint => new Uri(endpoint).Scheme != "https"))
        {
            throw new ArgumentException("Market HTTP endpoints require HTTPS.");
        }
        if (new[] { SolanaWebSocketUrl, BinanceWebSocketUrl }.Any(endpoint => new Uri(endpoint).Scheme != "wss"))
        {
            throw new ArgumentException("Market sockets require WSS.");
        }
    }
}
