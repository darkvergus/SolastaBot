namespace SolastaBot.Host.Portfolio;

public sealed record ResearchResult(string Id, string Strategy, DateTimeOffset CreatedAt, decimal TrainingPnl, decimal TestPnl, int TestTrades, string Verdict, int Observations, string DataHash, StrategySettings Candidate)
{
    public decimal BaselineTestPnl { get; init; }
    public decimal TestFees { get; init; }
    public decimal TestFunding { get; init; }
    public decimal TestDrawdown { get; init; }
    public int CoveredDays { get; init; }
    public int FeedErrors { get; init; }
}
