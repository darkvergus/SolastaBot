using SolastaBot.Core.Strategy;

namespace SolastaBot.Host.Portfolio;

public sealed record StrategySettings
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public int Version { get; init; } = 1;
    public decimal InitialCapital { get; init; }
    public string Symbol { get; init; } = "BTCUSDT";
    public string Interval { get; init; } = "4h";
    public decimal TradeSizeSol { get; init; } = 0.01m;
    public decimal TakeProfitFraction { get; init; } = 1m;
    public decimal PartialFraction { get; init; } = 0.5m;
    public decimal TrailFraction { get; init; } = 0.25m;
    public decimal StopFraction { get; init; } = 0.5m;
    public int HoldSeconds { get; init; } = 300;
    public int MaxPositions { get; init; } = 3;
    public decimal MaxPositionFraction { get; init; } = 0.05m;
    public decimal DailyLossFraction { get; init; } = 0.05m;
    public decimal SlippageBasisPoints { get; init; } = 300m;
    public decimal FeeBasisPoints { get; init; } = 125m;
    public decimal TransactionCostSol { get; init; } = 0.00001m;
    public decimal SetupCostSol { get; init; } = 0.0025m;
    public decimal MinimumVirtualSol { get; init; } = 31.04m;
    public decimal MinimumRealSol { get; init; } = 1m;
    public int EntryDelaySeconds { get; init; } = 5;
    public int ExitDelaySeconds { get; init; } = 1;
    public int MaximumQuoteAgeSeconds { get; init; } = 30;
    public bool Trial { get; init; }
    public TrendBandOptions Trend { get; init; } = new();
    public string Asset => Kind.StartsWith("solana-", StringComparison.Ordinal) ? "SOL" : "USDT";

    public void Validate()
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(Id, "^[a-z][a-z0-9-]{0,63}$") || Kind is not ("solana-launch" or "solana-continuation" or "binance-ema" or "binance-trend"))
        {
            throw new ArgumentException("Invalid strategy ID or kind.");
        }
        if (Version < 1 || InitialCapital <= 0m || TradeSizeSol <= 0m || MaxPositions < 1 || HoldSeconds < 1 || EntryDelaySeconds < 0 || ExitDelaySeconds < 1 || MaximumQuoteAgeSeconds < 1 || TakeProfitFraction <= 0m || MinimumRealSol < 0m || MinimumVirtualSol <= 0m || TransactionCostSol < 0m || SetupCostSol < 0m)
        {
            throw new ArgumentException("Invalid strategy capital, sizing, or timing.");
        }
        if (new[] { PartialFraction, TrailFraction, StopFraction, MaxPositionFraction, DailyLossFraction }.Any(fraction => fraction <= 0m || fraction >= 1m))
        {
            throw new ArgumentException("Strategy fractions must be between zero and one.");
        }
        if (SlippageBasisPoints < 0m || SlippageBasisPoints >= 10_000m || FeeBasisPoints < 0m || FeeBasisPoints >= 10_000m || Interval is not ("1h" or "4h") || !System.Text.RegularExpressions.Regex.IsMatch(Symbol, "^[A-Z0-9]{2,30}USDT$"))
        {
            throw new ArgumentException("Invalid market or cost settings.");
        }
        Trend.Validate();
    }
}
