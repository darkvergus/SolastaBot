using SolastaBot.Core.Domain;

namespace SolastaBot.Host.Portfolio;

public sealed class StrategyAccount
{
    public required StrategySettings Settings { get; set; }
    public decimal Cash { get; set; }
    public decimal NetContributions { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal DistributedHighWater { get; set; }
    public decimal Day { get; set; }
    public decimal DayOpeningEquity { get; set; }
    public bool DailyHalt { get; set; }
    public bool Paused { get; set; }
    public bool Flatten { get; set; }
    public decimal TrialStartedAt { get; set; }
    public bool TrialCompleted { get; set; }
    public int ConsecutiveLosses { get; set; }
    public int BlockedDirection { get; set; }
    public string LastDecision { get; set; } = "Waiting for market data";
    public decimal LastObservedAt { get; set; }
    public decimal LastFundingAt { get; set; }
    public int ClosedTrades { get; set; }
    public decimal PositivePnl { get; set; }
    public decimal NegativePnl { get; set; }
    public decimal TotalFees { get; set; }
    public decimal TotalFunding { get; set; }
    public decimal PeakEquity { get; set; }
    public decimal MaxDrawdown { get; set; }
    public List<PortfolioPosition> Positions { get; set; } = [];
    public List<PortfolioOrder> Orders { get; set; } = [];
    public HashSet<string> EnteredMarkets { get; set; } = [];
    public List<Candle> Candles { get; set; } = [];
    public List<PerpetualExposure> Exposures { get; set; } = [];
    public Dictionary<string, decimal> ProfitSplit { get; set; } = [];
    public decimal Reserved => Orders.Sum(order => order.Reserved);
    public decimal Available => Cash - Reserved;
    public decimal Equity => Cash + Positions.Sum(position => position.Mark);
}
