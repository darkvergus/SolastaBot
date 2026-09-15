namespace SolastaBot.Host.Portfolio;

public sealed class PortfolioState
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public bool Halted { get; set; }
    public decimal Now { get; set; }
    public string ConfigurationHash { get; set; } = "";
    public Dictionary<string, StrategyAccount> Strategies { get; set; } = [];
    public Dictionary<string, decimal> Reserves { get; set; } = [];
    public Dictionary<string, decimal> PendingAllocations { get; set; } = [];
    public Dictionary<string, TrackedMarket> Markets { get; set; } = [];
    public Dictionary<string, string> Health { get; set; } = [];
    public List<LedgerEntry> RecentEvents { get; set; } = [];
    public List<ResearchResult> Research { get; set; } = [];
    public HashSet<string> ConversionReferences { get; set; } = [];

    public static PortfolioState Create(UnifiedSettings settings)
    {
        settings.Validate();
        PortfolioState state = new();
        foreach (StrategySettings strategy in settings.Strategies)
        {
            state.Strategies.Add(strategy.Id, new() { Settings = strategy, Cash = strategy.InitialCapital, NetContributions = strategy.InitialCapital, PeakEquity = strategy.InitialCapital });
        }
        return state;
    }
}
