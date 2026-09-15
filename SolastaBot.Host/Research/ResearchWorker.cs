using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Research;

public sealed class ResearchWorker(UnifiedSettings settings, PortfolioStore store, ILogger<ResearchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string run = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (now.Hour >= settings.ResearchHourUtc && store.Snapshot().Health.GetValueOrDefault("research-day") != run)
            {
                try
                {
                    await Task.Run(() => Run(now, stoppingToken), stoppingToken);
                    store.Mutate(state => state.Health["research-day"] = run);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Research failed; trading continues");
                    store.Mutate(state => state.Health["research"] = exception.Message);
                }
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    public void Run(DateTimeOffset now, CancellationToken cancellationToken)
    {
        decimal end = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeSeconds();
        decimal split = end - 7m * 86400m;
        decimal start = split - 28m * 86400m;
        foreach (StrategyAccount source in store.Snapshot().Strategies.Values.Where(account => !account.Settings.Trial))
        {
            StrategySettings[] candidates = Candidates(source.Settings);
            List<WindowEvaluation> training = [];
            foreach (StrategySettings candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                training.Add(Evaluate(candidate, start, split, cancellationToken));
            }
            WindowEvaluation winner = training.OrderByDescending(result => result.Account.Equity - result.Settings.InitialCapital).ThenBy(result => JsonSerializer.Serialize(result.Settings), StringComparer.Ordinal).First();
            WindowEvaluation test = Evaluate(winner.Settings, split, end, cancellationToken);
            WindowEvaluation baseline = JsonSerializer.Serialize(source.Settings) == JsonSerializer.Serialize(winner.Settings) ? test : Evaluate(source.Settings, split, end, cancellationToken);
            decimal trainPnl = winner.Account.Equity - winner.Settings.InitialCapital;
            decimal testPnl = test.Account.Equity - test.Settings.InitialCapital;
            bool coverage = winner.First <= start + 86400m && winner.Last >= split - 86400m && test.First <= split + 86400m && test.Last >= end - 86400m && winner.CoveredDays == 28 && test.CoveredDays == 7 && winner.GapCount <= winner.Count * 0.01m && test.GapCount <= test.Count * 0.01m;
            bool eligible = coverage && trainPnl > 0m && testPnl > 0m && test.Account is { ClosedTrades: >= 20, Positions.Count: 0 };
            string verdict = !coverage ? "Insufficient coverage: need 28 training days and 7 subsequent test days" : eligible ? "Eligible for independent paper trial; not approved for real funds" : "No paper promotion: return, trade count, or unresolved-position check failed";
            string id = $"{source.Settings.Id}-{now:yyyyMMdd}";
            ResearchResult result = new(id, source.Settings.Id, now, trainPnl, testPnl, test.Account.ClosedTrades, verdict, winner.Count + test.Count, winner.Hash + ":" + test.Hash, winner.Settings)
            {
                BaselineTestPnl = baseline.Account.Equity - baseline.Settings.InitialCapital, TestFees = test.Account.TotalFees, TestFunding = test.Account.TotalFunding,
                TestDrawdown = test.Account.MaxDrawdown, CoveredDays = winner.CoveredDays + test.CoveredDays, FeedErrors = winner.GapCount + test.GapCount
            };
            store.Mutate(state =>
            {
                if (state.Research.Any(existing => existing.Id == id))
                {
                    return;
                }

                state.Research.Add(result);
                state.Health["research"] = verdict;
                if (eligible && state.Strategies.Values.Count(account => account.Settings.Trial && !account.TrialCompleted) < 8)
                {
                    string sourcePrefix = source.Settings.Id.Length > 32 ? source.Settings.Id[..32] : source.Settings.Id;
                    string sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Settings.Id)))[..8].ToLowerInvariant();
                    StrategySettings trial = winner.Settings with { Id = $"trial-{sourcePrefix}-{sourceHash}-{now:yyyyMMdd}", Trial = true, Version = source.Settings.Version + 1 };
                    trial.Validate();
                    StrategyAccount account = new() { Settings = trial, Cash = trial.InitialCapital, NetContributions = trial.InitialCapital, PeakEquity = trial.InitialCapital, Candles = [.. source.Candles], TrialStartedAt = now.ToUnixTimeMilliseconds() / 1000m };
                    state.Strategies.Add(trial.Id, account);
                    PortfolioEngine.Record(state, account, "TrialStarted", "", 0m, 0m, "Independent virtual balance; no managed capital allocated");
                }
            });
        }
    }

    private WindowEvaluation Evaluate(StrategySettings candidate, decimal from, decimal to, CancellationToken cancellationToken)
    {
        PortfolioState replay = PortfolioState.Create(new() { Strategies = [candidate] });
        PortfolioEngine engine = new();
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0;
        decimal first = decimal.MaxValue;
        decimal last = 0m;
        int gapCount = 0;
        HashSet<decimal> days = [];
        foreach (MarketObservation observation in store.ReadObservations(from, to))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool relevant = candidate.Asset == "SOL" ? observation.Source.StartsWith("solana", StringComparison.Ordinal) : (observation.Market == candidate.Symbol || observation.Market == "") && observation.Source.StartsWith("binance", StringComparison.Ordinal);
            if (!relevant)
            {
                continue;
            }

            digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(observation));
            engine.Tick(replay, observation.At);
            engine.Apply(replay, observation);
            if (observation.Error is not null)
            {
                gapCount++;
            }

            if (observation.Error is null && !observation.Warmup && (observation.Curve is not null || observation is { Price: > 0m, FundingRate: null }))
            {
                count++;
                first = Math.Min(first, observation.At);
                last = Math.Max(last, observation.At);
                days.Add(decimal.Floor(observation.At / 86400m));
            }
            if (replay.RecentEvents.Count > 100)
            {
                replay.RecentEvents = [.. replay.RecentEvents.TakeLast(100)];
            }

            foreach (string mint in replay.Markets.Where(pair => observation.At - pair.Value.Launch.CreatedAt > 10800m && !replay.Strategies[candidate.Id].Positions.Any(position => position.Market == pair.Key)).Select(pair => pair.Key).ToArray())
            {
                replay.Markets.Remove(mint);
            }
        }
        return new(candidate, replay.Strategies[candidate.Id], count, first, last, days.Count, gapCount, Convert.ToHexString(digest.GetHashAndReset()));
    }

    private static StrategySettings[] Candidates(StrategySettings source)
    {
        StrategySettings[] alternatives = source.Asset == "SOL"
            ? [source with { TrailFraction = 0.20m }, source with { TrailFraction = 0.25m }, source with { TrailFraction = 0.30m }]
            : source.Kind == "binance-trend"
                ? [source with { Trend = source.Trend with { EntryBandBasisPoints = 40m } }, source with { Trend = source.Trend with { EntryBandBasisPoints = 50m } }, source with { Trend = source.Trend with { EntryBandBasisPoints = 60m } }]
                : [source with { Trend = source.Trend with { SlowPeriod = 44 } }, source with { Trend = source.Trend with { SlowPeriod = 55 } }, source with { Trend = source.Trend with { SlowPeriod = 66 } }];
        return
        [
            .. alternatives.Append(source).Where(candidate => candidate.Trend.FastPeriod < candidate.Trend.SlowPeriod).DistinctBy(candidate => JsonSerializer.Serialize(candidate))
        ];
    }
}
