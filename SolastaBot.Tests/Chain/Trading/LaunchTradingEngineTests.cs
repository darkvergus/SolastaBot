using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;

namespace SolastaBot.Tests.Chain.Trading;

public sealed class LaunchTradingEngineTests
{
    [Fact]
    public void LaunchLeadsToADelayedBuyAndTimeExitAtALaterObservation()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTransition pending = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new());

        Assert.Single(pending.State.Pending);
        Assert.Empty(pending.State.Positions);
        Assert.Equal(10m, pending.State.CashSol);

        PaperTransition bought = engine.Observe(pending.State, TradingFixture.Curve(105m), new());
        Assert.Empty(bought.State.Pending);
        Assert.Single(bought.State.Positions);
        Assert.Equal(8.96m, bought.State.CashSol);
        Assert.Contains(bought.Events, tradingEvent => tradingEvent.Kind == "PaperBuy");

        PaperTransition signalled = engine.Tick(bought.State, 110m, new());
        Assert.Single(signalled.State.Positions);
        PaperTransition sold = engine.Observe(signalled.State, TradingFixture.Curve(111m, quoteSol: 64m, tokens: 500m), new());
        Assert.Empty(sold.State.Positions);
        Assert.Contains(sold.Events, tradingEvent => tradingEvent.Kind == "PaperSell");
        Assert.True(sold.State.CashSol > 10m);
    }

    [Fact]
    public void SameObservationCannotFillAZeroDelayEntry()
    {
        PaperTradingOptions options = TradingFixture.Options with { Execution = ChainFixture.Options with { EntryDelaySeconds = 0m } };
        LaunchTradingEngine engine = new(options);
        PaperTransition pending = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new());
        PaperTransition unchanged = engine.Observe(pending.State, TradingFixture.Curve(100m), new());

        Assert.Empty(unchanged.State.Positions);
        Assert.Single(unchanged.State.Pending);
    }

    [Fact]
    public void HaltIsCheckedAgainBeforeAFillAndCancelsPendingEntries()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTransition pending = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new());
        PaperTransition halted = engine.Observe(pending.State, TradingFixture.Curve(105m), new(HaltEntries: true));

        Assert.Empty(halted.State.Positions);
        Assert.Empty(halted.State.Pending);
        Assert.Equal(10m, halted.State.CashSol);
        Assert.DoesNotContain(halted.Events, tradingEvent => tradingEvent.Kind == "PaperBuy");
    }

    [Fact]
    public void PendingOrdersReserveChainCashWithoutBorrowingPerpFunds()
    {
        PaperTradingOptions options = TradingFixture.Options with { MaxPositions = 20 };
        LaunchTradingEngine engine = new(options);
        PaperTradingState state = PaperTradingState.Create(100m, options);
        for (int index = 0; index < 15; index++)
        {
            state = engine.Discover(state, TradingFixture.Launch(mint: $"mint-{index}"), new()).State;
        }

        Assert.Equal(9, state.Pending.Count);
        Assert.Equal(10m, state.CashSol);
        Assert.True(state.Pending.Count * options.EntryCostSol <= options.Capital.ChainCapitalSol);
        Assert.Equal(50m, options.Capital.PerpReserveSol);
    }

    [Fact]
    public void PositionLimitIncludesPendingEntries()
    {
        PaperTradingOptions options = TradingFixture.Options with { MaxPositions = 1 };
        LaunchTradingEngine engine = new(options);
        PaperTransition first = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new());
        PaperTransition second = engine.Discover(first.State, TradingFixture.Launch(mint: "second-mint"), new());

        Assert.Single(second.State.Pending);
        Assert.Contains(second.Events, tradingEvent => tradingEvent.Reason == "Position limit");
    }

    [Fact]
    public void ExpiredEntryIsReleasedWithoutBuyingAStaleOpportunity()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTransition pending = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new());
        PaperTransition expired = engine.Observe(pending.State, TradingFixture.Curve(136m), new());

        Assert.Empty(expired.State.Pending);
        Assert.Empty(expired.State.Positions);
        Assert.Equal(10m, expired.State.CashSol);
    }

    [Fact]
    public void FlattenContinuesTryingAfterAnUnfilledSellWithoutDeletingThePosition()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTradingState opened = TradingFixture.Open(engine, options);
        PaperTransition signal = engine.Tick(opened, 106m, new(Flatten: true));
        CurveObservation empty = TradingFixture.Curve(107m) with { RealQuoteReserves = 0m };
        PaperTransition failed = engine.Observe(signal.State, empty, new(Flatten: true));

        Assert.Single(failed.State.Positions);
        Assert.Equal(opened.CashSol, failed.State.CashSol);
        Assert.Contains(failed.Events, tradingEvent => tradingEvent.Kind == "ExitUnfilled");

        PaperTransition sold = engine.Observe(failed.State, TradingFixture.Curve(108m), new(Flatten: true));
        Assert.Empty(sold.State.Positions);
        Assert.True(sold.State.CashSol > failed.State.CashSol);
    }

    [Fact]
    public void MigrationRemainsUnresolvedAndBlocksNewEntries()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTradingState opened = TradingFixture.Open(engine, options);
        PaperTransition migrated = engine.Observe(opened, TradingFixture.Curve(106m) with { Complete = true }, new());
        PaperTransition later = engine.Discover(migrated.State, TradingFixture.Launch(107m, "second-mint"), new());

        Assert.True(Assert.Single(later.State.Positions).Migrated);
        Assert.Equal(0m, later.State.Positions[0].MarkSol);
        Assert.Empty(later.State.Pending);
        Assert.Equal(opened.CashSol, later.State.CashSol);
    }

    [Fact]
    public void DailyLossHaltReleasesAtTheNextUtcDay()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTradingState opened = TradingFixture.Open(engine, options);
        PaperTransition loss = engine.Observe(opened, TradingFixture.Curve(106m, quoteSol: 0.1m, tokens: 320000m), new());

        Assert.True(loss.State.DailyHalt);
        Assert.NotNull(Assert.Single(loss.State.Positions).ExitRequestedAt);

        PaperTransition nextDay = engine.Tick(loss.State, 86400m, new());
        Assert.False(nextDay.State.DailyHalt);
        Assert.Equal(1m, nextDay.State.Day);
    }

    [Fact]
    public void ReleasingManualHaltAllowsNewSignals()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTransition halted = engine.Discover(PaperTradingState.Create(100m, options), TradingFixture.Launch(), new(HaltEntries: true));
        PaperTransition resumed = engine.Discover(halted.State, TradingFixture.Launch(101m, "new-mint"), new());

        Assert.Empty(halted.State.Pending);
        Assert.Single(resumed.State.Pending);
    }

    [Fact]
    public void StalePricesPreventNewEntriesButManagementResumesOnFreshData()
    {
        PaperTradingOptions options = TradingFixture.Options;
        LaunchTradingEngine engine = new(options);
        PaperTradingState opened = TradingFixture.Open(engine, options);
        PaperTransition stale = engine.Discover(opened, TradingFixture.Launch(136m, "second-mint"), new());

        Assert.Empty(stale.State.Pending);
        Assert.Contains(stale.Events, tradingEvent => tradingEvent.Reason.Contains("stale", StringComparison.Ordinal));

        PaperTransition recovered = engine.Observe(stale.State, TradingFixture.Curve(137m), new());
        Assert.Empty(recovered.State.Positions);
        Assert.Contains(recovered.Events, tradingEvent => tradingEvent.Kind == "PaperSell");
    }

    [Fact]
    public void InvalidAllocationsAndFundedModeFailBeforeStarting()
    {
        Assert.Throws<ArgumentException>(() => (TradingFixture.Options with { Mode = "Live" }).Validate());
        Assert.Throws<ArgumentException>(() => new CapitalAllocation { ChainFraction = 0.8m, PerpFraction = 0.5m }.Validate());
        Assert.Throws<ArgumentException>(() => (TradingFixture.Options with { MaxPositionFraction = 0.01m }).Validate());
    }
}
