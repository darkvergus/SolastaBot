namespace SolastaBot.ChainCollector;

public sealed class CollectorOptions
{
    public string ApiBase { get; init; } = "https://frontend-api-v3.pump.fun";
    public string SolQuoteMint { get; init; } = "11111111111111111111111111111111";
    public double FeedEverySeconds { get; init; } = 20d;
    public double RateLimit { get; init; } = 0.80d;
    public int ActiveCap { get; init; } = 70;
    public decimal BuyInRatio { get; init; } = 31.04m / 30m;
    public int BaselineMinimum { get; init; } = 40;
    public int BaselineCapacity { get; init; } = 600;
    public int SeenCapacity { get; init; } = 500_000;
    public double ControlRate { get; init; } = 1d / 890d;

    public IReadOnlyList<PollScheduleEntry> Schedule { get; init; } = new List<PollScheduleEntry>
    {
        new(0, 120, 3),
        new(120, 600, 15),
        new(600, 3600, 60),
        new(3600, 10800, 300)
    };

    public IReadOnlyDictionary<string, double> AdmissionRates { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["filtered_sol"] = 1d,
        ["filtered_alt"] = 0.40d,
        ["control"] = 1d
    };

    public string FeedUrl => $"{ApiBase}/coins?offset=0&limit=50&sort=created_timestamp&order=DESC&includeNsfw=true";

    public int TrackForSeconds => Schedule[^1].ToSeconds;

    public int PollsPerMint => Schedule.Sum(scheduleEntry => (scheduleEntry.ToSeconds - scheduleEntry.FromSeconds) / scheduleEntry.EverySeconds);
}