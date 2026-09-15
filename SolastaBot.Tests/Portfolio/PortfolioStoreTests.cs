using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SolastaBot.Host.Portfolio;
using SolastaBot.Host.Research;

namespace SolastaBot.Tests.Portfolio;

public sealed class PortfolioStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "solasta-portfolio-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void JournalAndReplayProduceIdenticalDecisions()
    {
        using PortfolioStore store = new(directory, new());
        PortfolioState replay = PortfolioState.Create(new());
        PortfolioEngine engine = new();
        MarketObservation[] observations = [PortfolioEngineTests.Discovery(), PortfolioEngineTests.Quote(1005m, 40m), PortfolioEngineTests.Quote(1006m, 160m), PortfolioEngineTests.Quote(1007m, 160m), PortfolioEngineTests.Quote(1008m, 240m), PortfolioEngineTests.Quote(1009m, 100m), PortfolioEngineTests.Quote(1010m, 80m)];
        foreach (MarketObservation observation in observations)
        {
            store.Observe(observation);
        }

        foreach (MarketObservation observation in store.ReadObservations(0m, 2000m))
        {
            engine.Apply(replay, observation);
        }

        Assert.Equal(JsonSerializer.Serialize(replay.RecentEvents), JsonSerializer.Serialize(store.Snapshot().RecentEvents));
        Assert.Equal(replay.Strategies["launch"].Cash, store.Snapshot().Strategies["launch"].Cash);
    }

    [Fact]
    public void FailedMutationDoesNotChangeTheJournal()
    {
        using PortfolioStore store = new(directory, new());
        long revision = store.Snapshot().Revision;
        Assert.Throws<InvalidOperationException>(() => store.Mutate(state => { state.Strategies["launch"].Cash = 0m; throw new InvalidOperationException("Injected failure"); }));
        Assert.Equal(5m, store.Snapshot().Strategies["launch"].Cash);
        Assert.Equal(revision, store.Snapshot().Revision);
    }

    [Fact]
    public void RestartDoesNotRedistributeProfit()
    {
        using (PortfolioStore store = new(directory, new()))
        {
            store.Mutate(state =>
            {
                StrategyAccount source = state.Strategies["launch"];
                source.RealizedPnl = 1m;
                source.Cash += 1m;
                store.Engine.ConfigureSplit(state, "launch", new() { ["reserve"] = 1m });
                store.Engine.Distribute(state, source);
            });
        }
        using PortfolioStore restored = new(directory, new());
        restored.Mutate(state => restored.Engine.Distribute(state, state.Strategies["launch"]));
        Assert.Equal(1m, restored.Snapshot().Reserves["SOL"]);
        Assert.Equal(5m, restored.Snapshot().Strategies["launch"].Cash);
    }

    [Fact]
    public async Task ConcurrentTransfersCannotOverspend()
    {
        using PortfolioStore store = new(directory, new());
        int successes = 0;
        Task[] attempts =
        [
            .. Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
            {
                try
                {
                    store.Mutate(state => store.Engine.TransferBudget(state, "launch", "continuation", 1m));
                    Interlocked.Increment(ref successes);
                }
                catch (ArgumentException)
                {
                }
            }))
        ];
        await Task.WhenAll(attempts);
        Assert.Equal(5, successes);
        Assert.Equal(0m, store.Snapshot().Strategies["launch"].Cash);
        Assert.Equal(10m, store.Snapshot().Strategies["continuation"].Cash);
    }

    [Fact]
    public void ExistingJournalCannotBeOpenedWithDifferentCapital()
    {
        using (PortfolioStore store = new(directory, new())) { }
        Assert.Throws<InvalidOperationException>(() => new PortfolioStore(directory, new() { Strategies = [new() { Id = "launch", Kind = "solana-launch", InitialCapital = 50m }] }));
    }

    [Fact]
    public void ResearchWithoutCoverageDoesNotInventAPaperTrial()
    {
        using PortfolioStore store = new(directory, new());
        ResearchWorker worker = new(new(), store, NullLogger<ResearchWorker>.Instance);
        worker.Run(new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero), CancellationToken.None);
        Assert.Equal(4, store.Snapshot().Research.Count);
        Assert.All(store.Snapshot().Research, result => Assert.StartsWith("Insufficient coverage", result.Verdict));
        Assert.DoesNotContain(store.Snapshot().Strategies.Values, account => account.Settings.Trial);
    }

    [Fact]
    public void ChangingConnectivityDoesNotResetBudgetsOrControls()
    {
        using (PortfolioStore store = new(directory, new()))
        {
            store.Mutate(state => state.Strategies["launch"].Paused = true);
        }
        using PortfolioStore restored = new(directory, new() { ListenUrl = "http://127.0.0.1:5099", EnableNetwork = false, TrackingCapacity = 3 });
        Assert.True(restored.Snapshot().Strategies["launch"].Paused);
        Assert.Equal(5m, restored.Snapshot().Strategies["launch"].Cash);
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
