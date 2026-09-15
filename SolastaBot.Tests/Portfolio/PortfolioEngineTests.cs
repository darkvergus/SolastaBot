using System.Text.Json;
using SolastaBot.Chain.Trading;
using SolastaBot.Core.Domain;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Tests.Portfolio;

public sealed class PortfolioEngineTests
{
    [Fact]
    public void PartialProfitThenTrailingExitRetainsOwnershipUntilTheNextQuote()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        Open(engine, state);
        PortfolioPosition initial = Assert.Single(state.Strategies["launch"].Positions);
        decimal quantity = initial.Quantity;
        engine.Apply(state, Quote(1006m, 160m));
        Assert.Equal(quantity, Assert.Single(state.Strategies["launch"].Positions).Quantity);
        engine.Apply(state, Quote(1007m, 160m));
        PortfolioPosition remainder = Assert.Single(state.Strategies["launch"].Positions);
        Assert.True(remainder.PartialTaken);
        Assert.InRange(remainder.Quantity, quantity / 2m, quantity / 2m + 1m);
        engine.Apply(state, Quote(1008m, 240m));
        engine.Apply(state, Quote(1009m, 120m));
        Assert.Equal("Trailing exit", remainder.ExitReason);
        Assert.Single(state.Strategies["launch"].Positions);
        engine.Apply(state, Quote(1010m, 100m));
        Assert.Empty(state.Strategies["launch"].Positions);
        Assert.True(state.Strategies["launch"].RealizedPnl > 0m);
        Assert.Equal(2, state.RecentEvents.Count(entry => entry.Kind == "Sell"));
        Assert.Equal(1, state.Strategies["launch"].ClosedTrades);
    }

    [Fact]
    public void MissingLiquidityDoesNotInventASale()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        Open(engine, state);
        state.Strategies["launch"].Flatten = true;
        engine.Tick(state, 1006m);
        MarketObservation unavailable = Quote(1007m, 40m);
        engine.Apply(state, unavailable with { Curve = unavailable.Curve! with { RealQuoteReserves = 0m } });
        Assert.Single(state.Strategies["launch"].Positions);
        Assert.Equal(0m, state.Strategies["launch"].Positions[0].Mark);
        Assert.DoesNotContain(state.RecentEvents, entry => entry.Kind == "Sell");
    }

    [Fact]
    public void TrailingStateAndPartialProgressSurviveSerialization()
    {
        PortfolioEngine engine = new();
        PortfolioState original = Create();
        Open(engine, original);
        engine.Apply(original, Quote(1006m, 160m));
        engine.Apply(original, Quote(1007m, 160m));
        PortfolioState restored = JsonSerializer.Deserialize<PortfolioState>(JsonSerializer.Serialize(original))!;
        foreach (MarketObservation observation in new[] { Quote(1008m, 240m), Quote(1009m, 100m), Quote(1010m, 80m) })
        {
            engine.Apply(original, observation);
            new PortfolioEngine().Apply(restored, observation);
        }
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public void SharedMintPositionsCannotSellEachOthersQuantity()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new() { Strategies = [new() { Id = "first", Kind = "solana-launch", InitialCapital = 5m }, new() { Id = "second", Kind = "solana-launch", InitialCapital = 5m }] });
        engine.Apply(state, Discovery());
        engine.Apply(state, Quote(1005m, 40m));
        decimal secondQuantity = Assert.Single(state.Strategies["second"].Positions).Quantity;
        state.Strategies["first"].Flatten = true;
        engine.Tick(state, 1006m);
        engine.Apply(state, Quote(1007m, 40m));
        Assert.Empty(state.Strategies["first"].Positions);
        Assert.Equal(secondQuantity, Assert.Single(state.Strategies["second"].Positions).Quantity);
    }

    [Fact]
    public void RepeatedQuoteCannotFillOrSellTwice()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        Open(engine, state);
        engine.Apply(state, Quote(1005m, 40m));
        Assert.Single(state.RecentEvents, entry => entry.Kind == "Buy");
        Assert.Single(state.Strategies["launch"].Positions);
    }

    [Fact]
    public void ReservedEntryFundsCannotBeTransferred()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new());
        engine.Apply(state, Discovery());
        StrategyAccount source = state.Strategies["launch"];
        Assert.True(source.Reserved > 0m);
        Assert.Throws<ArgumentException>(() => engine.TransferBudget(state, "launch", "continuation", source.Cash));
    }

    [Fact]
    public void ProfitDistributionRecoversLossesAndRunsOnlyOnce()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new());
        StrategyAccount source = state.Strategies["launch"];
        source.Cash = 8m;
        source.RealizedPnl = 3m;
        engine.ConfigureSplit(state, "launch", new() { ["retain"] = 0.5m, ["reserve"] = 0.25m, ["continuation"] = 0.25m });
        engine.Distribute(state, source);
        Assert.Equal(6.5m, source.Cash);
        Assert.Equal(5.75m, state.Strategies["continuation"].Cash);
        Assert.Equal(0.75m, state.Reserves["SOL"]);
        engine.Distribute(state, source);
        source.RealizedPnl = 1m;
        engine.Distribute(state, source);
        source.RealizedPnl = 3m;
        engine.Distribute(state, source);
        Assert.Equal(6.5m, source.Cash);
        Assert.Equal(3m, source.DistributedHighWater);
    }

    [Fact]
    public void CrossCurrencyShareDoesNotCreateSpendableUsdt()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new());
        StrategyAccount source = state.Strategies["launch"];
        source.Cash += 1m;
        source.RealizedPnl = 1m;
        engine.ConfigureSplit(state, "launch", new() { ["trend"] = 1m });
        engine.Distribute(state, source);
        Assert.Equal(1m, state.PendingAllocations["SOL:trend"]);
        Assert.Equal(5000m, state.Strategies["trend"].Cash);
        Assert.Throws<ArgumentException>(() => engine.TransferBudget(state, "launch", "trend", 1m));
    }

    [Fact]
    public void DailyHaltReleasesAtUtcMidnightButManualPauseRemains()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        StrategyAccount account = state.Strategies["launch"];
        engine.Tick(state, 1000m);
        account.Cash = 4m;
        account.Paused = true;
        engine.Tick(state, 1001m);
        Assert.True(account.DailyHalt);
        engine.Tick(state, 86400m);
        Assert.False(account.DailyHalt);
        Assert.True(account.Paused);
    }

    [Fact]
    public void HaltCancelsPendingEntriesWithoutTouchingHoldings()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        engine.Apply(state, Discovery());
        Assert.Single(state.Strategies["launch"].Orders);
        state.Halted = true;
        engine.Tick(state, 1001m);
        Assert.Empty(state.Strategies["launch"].Orders);
        Assert.Equal(5m, state.Strategies["launch"].Cash);
    }

    [Fact]
    public void PerpetualPendingOrderUsesSubsequentPriceAndInstrumentFilters()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new() { Strategies = [new() { Id = "trend", Kind = "binance-trend", InitialCapital = 10_000m, SlippageBasisPoints = 0m, FeeBasisPoints = 0m }] });
        StrategyAccount account = state.Strategies["trend"];
        account.Orders.Add(new("BTCUSDT", 1000m, 1000m, 1030m, 150m, 1, 10m));
        Instrument instrument = new("BTCUSDT", "BTC", "USDT", 0.1m, 0.001m, 0.001m, 1m, 1);
        engine.Apply(state, new() { Source = "binance-mark", Market = "BTCUSDT", At = 1000m, Price = 100m, Instrument = instrument });
        Assert.Empty(account.Positions);
        engine.Apply(state, new() { Source = "binance-mark", Market = "BTCUSDT", At = 1001m, Price = 150m, Instrument = instrument });
        Assert.Equal(150m, Assert.Single(account.Positions).EntryPrice);
        Assert.Equal(1m, account.Positions[0].Quantity);
    }

    [Fact]
    public void FundingPublishedAfterCloseStillChargesTheHeldPosition()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new());
        StrategyAccount account = state.Strategies["trend"];
        account.LastObservedAt = 30000m;
        account.Exposures.Add(new() { Market = "BTCUSDT", Quantity = 2m, Direction = 1, OpenedAt = 28000m, ClosedAt = 29000m });
        MarketObservation funding = new() { Source = "binance-funding", Market = "BTCUSDT", At = 28800m, Price = 100m, FundingRate = 0.01m };
        engine.Apply(state, funding);
        engine.Apply(state, funding);
        Assert.Equal(4998m, account.Cash);
        Assert.Equal(2m, account.TotalFunding);
        Assert.Equal(-2m, account.RealizedPnl);
    }

    [Fact]
    public void PaperConfigurationRejectsMainnetAndPublicDashboardBinding()
    {
        Assert.Throws<ArgumentException>(() => new UnifiedSettings { Mode = "Mainnet" }.Validate());
        Assert.Throws<ArgumentException>(() => new UnifiedSettings { ListenUrl = "http://0.0.0.0:5080" }.Validate());
    }

    [Fact]
    public void SparseContinuationSamplesCannotManufactureCompleteBars()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new() { Strategies = [new() { Id = "continuation", Kind = "solana-continuation", InitialCapital = 5m }] });
        engine.Apply(state, Discovery());
        for (int minute = 1; minute < 20; minute++)
        {
            engine.Apply(state, Quote(1000m + minute * 60m, 40m + minute));
        }

        Assert.Empty(state.Strategies["continuation"].Orders);
        Assert.Empty(state.Strategies["continuation"].Positions);
        Assert.Empty(state.Markets["mint"].Bars);
    }

    public static PortfolioState Create() => PortfolioState.Create(new() { Strategies = [new() { Id = "launch", Kind = "solana-launch", InitialCapital = 5m }] });

    [Fact]
    public void ContinuationWaitsForTheBreakoutBarToCloseBeforeQueuingEntry()
    {
        PortfolioEngine engine = new();
        PortfolioState state = PortfolioState.Create(new() { Strategies = [new() { Id = "continuation", Kind = "solana-continuation", InitialCapital = 5m }] });
        engine.Apply(state, Discovery());
        for (int at = 1020; at <= 1675; at += 5)
        {
            engine.Apply(state, Quote(at, at >= 1620 ? 80m : 40m));
        }

        Assert.Empty(state.Strategies["continuation"].Orders);
        engine.Apply(state, Quote(1680m, 80m));
        Assert.Single(state.Strategies["continuation"].Orders);
        Assert.Empty(state.Strategies["continuation"].Positions);
        engine.Apply(state, Quote(1685m, 80m));
        Assert.Single(state.Strategies["continuation"].Positions);
    }

    [Fact]
    public void ConversionRequiresPendingFundsAndCannotBeRecordedTwice()
    {
        PortfolioState state = PortfolioState.Create(new());
        PortfolioEngine engine = new();
        state.PendingAllocations["SOL:trend"] = 1m;
        engine.RecordConversion(state, "SOL:trend", 0.5m, 70m, "receipt-one");
        Assert.Equal(5070m, state.Strategies["trend"].Cash);
        Assert.Equal(0m, state.Strategies["trend"].RealizedPnl);
        Assert.Equal(0.5m, state.PendingAllocations["SOL:trend"]);
        Assert.Throws<ArgumentException>(() => engine.RecordConversion(state, "SOL:trend", 0.5m, 70m, "receipt-one"));
        Assert.Throws<ArgumentException>(() => engine.RecordConversion(state, "SOL:trend", 1m, 140m, "receipt-two"));
    }

    [Fact]
    public void FundingLossCancelsUnaffordableReservationsWithoutBreakingTheJournal()
    {
        PortfolioState state = PortfolioState.Create(new());
        PortfolioEngine engine = new();
        StrategyAccount account = state.Strategies["trend"];
        account.Cash = 10m;
        account.Orders.Add(new("BTCUSDT", 28800m, 28801m, 28830m, 9m));
        account.Exposures.Add(new() { Market = "BTCUSDT", Quantity = 1m, Direction = 1, OpenedAt = 28000m, ClosedAt = 29000m });
        engine.Apply(state, new() { Source = "binance-funding", Market = "BTCUSDT", At = 28800m, Price = 100m, FundingRate = 0.02m });
        Assert.Equal(8m, account.Cash);
        Assert.Empty(account.Orders);
        PortfolioEngine.AssertOwnership(state);
    }

    [Fact]
    public void BufferedStaleQuoteCannotFillAnEntry()
    {
        PortfolioEngine engine = new();
        PortfolioState state = Create();
        engine.Apply(state, Discovery());
        engine.Apply(state, Quote(1005m, 40m) with { ReceivedAt = 1100m });
        Assert.Empty(state.Strategies["launch"].Positions);
        Assert.Equal("Delayed observation rejected", state.Strategies["launch"].LastDecision);
    }

    [Fact]
    public void PaperTrialExpiresWithoutMovingAnyManagedFunds()
    {
        PortfolioState state = PortfolioState.Create(new() { Strategies = [new() { Id = "trial", Kind = "solana-launch", InitialCapital = 5m, Trial = true }] });
        PortfolioEngine engine = new();
        engine.Tick(state, 1000m);
        engine.Tick(state, 1000m + 7m * 86400m);
        Assert.True(state.Strategies["trial"].TrialCompleted);
        Assert.True(state.Strategies["trial"].Paused);
        Assert.Empty(state.Reserves);
        Assert.Throws<ArgumentException>(() => engine.ConfigureSplit(state, "trial", new() { ["reserve"] = 1m }));
    }
    internal static void Open(PortfolioEngine engine, PortfolioState state)
    {
        engine.Apply(state, Discovery());
        engine.Apply(state, Quote(1005m, 40m));
    }
    public static MarketObservation Discovery()
    {
        MarketObservation observation = Quote(1000m, 40m);
        LaunchCandidate launch = new("mint", 1000m, "11111111111111111111111111111111", 9, "pump", true, true, observation.Curve!);
        return observation with { Source = "solana-launch", Launch = launch };
    }
    public static MarketObservation Quote(decimal at, decimal reserve) => new() { Source = "solana", Market = "mint", At = at, Curve = new("mint", at, false, reserve * 1_000_000_000m, 1_000_000_000_000m, 500_000_000_000m, 1_000_000_000_000m, null) };
}
