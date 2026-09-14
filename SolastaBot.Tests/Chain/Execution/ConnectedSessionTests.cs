using SolastaBot.Chain.Execution;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana;

namespace SolastaBot.Tests.Chain.Execution;

public sealed class ConnectedSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"solasta-execution-{Guid.NewGuid():N}");
    private readonly StubConnectedRouter router = new();
    private static TradingControl Running() => new();
    private static OrderIntent Buy(string id = "first") => new(id, "mint", OrderSide.Buy, 10_000_000, 100, "Test entry");

    internal static ConnectedOptions Options(string mode = "Devnet") => new()
    {
        Mode = mode,
        WalletPath = "outside-repo.json",
        Strategy = new()
        {
            Capital = new() { TotalPaperCapitalSol = 10, ChainFraction = 0.2m, PerpFraction = 0.5m },
            Execution = new() { FeeBasisPoints = 125, SlippageBasisPoints = 300, TransactionCostSol = 0.00001m, EntrySetupCostSol = 0.0025m }
        }
    };

    private async Task<ConnectedSession> OpenAsync(string mode = "Devnet")
    {
        ConnectedStateStore store = new(Path.Combine(directory, "connected.db"));
        return new(store, router, Options(mode), await store.OpenAsync("test-network-wallet", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcknowledgedSubmissionDoesNotCreateHoldingsAndRestartResendsIdenticalBytes()
    {
        ConnectedSession first = await OpenAsync();
        await first.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        Assert.Empty(first.State.Positions);
        Assert.Equal(20_000_000UL, first.State.ReservedLamports);
        ConnectedSession restarted = await OpenAsync();
        await restarted.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(1, router.Preparations);
        Assert.Equal(new[] { "signed-first", "signed-first" }, router.Submissions);
    }

    [Fact]
    public async Task TimeoutAfterAcceptanceCannotCreateAReplacementOrder()
    {
        router.ThrowOnSubmit = true;
        ConnectedSession session = await OpenAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken));
        ConnectedSession restarted = await OpenAsync();
        Assert.Equal(OrderStatus.Unresolved, Assert.Single(restarted.State.Orders).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExecuteAsync(Buy("replacement"), Running, TestContext.Current.CancellationToken));
        Assert.Equal(1, router.Preparations);
    }

    [Fact]
    public async Task CrashAfterSigningBeforeSendingRecoversPersistedTransaction()
    {
        ConnectedSession session = await OpenAsync();
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        ExecutionOrder signed = await router.PrepareAsync(Buy(), TestContext.Current.CancellationToken);
        await session.SaveAsync(session.State with { Orders = [signed] }, "SignedBeforeSubmission", TestContext.Current.CancellationToken);
        Assert.Empty(router.Submissions);
        ConnectedSession restarted = await OpenAsync();
        await restarted.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal("signed-first", Assert.Single(router.Submissions));
    }

    [Fact]
    public async Task FinalizedReceiptAppliesOnceAndUsesActualFeesAndTokens()
    {
        ConnectedSession session = await OpenAsync();
        await session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        router.Receipt = new(false, -12_044_280, 12345, 5000, 110);
        router.Wallet = new(987_955_720, new Dictionary<string, ulong> { ["mint"] = 12345 }, 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        ConnectedSession restarted = await OpenAsync();
        await restarted.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(12345UL, Assert.Single(restarted.State.Positions).Tokens);
        Assert.Equal(0.01204428m, restarted.State.Positions[0].CostSol);
        Assert.Equal(-12_044_280L, restarted.State.NetWalletChangeLamports);
        Assert.Equal(0UL, restarted.State.ReservedLamports);
        Assert.Null(restarted.State.ReconciliationError);
    }

    [Fact]
    public async Task FailedSellRetainsTokensAndChargesTheConfirmedNetworkFee()
    {
        ConnectedSession session = await WithPositionAsync();
        await session.ExecuteAsync(new("exit", "mint", OrderSide.Sell, 100, 101, "Flatten"), Running, TestContext.Current.CancellationToken);
        router.Receipt = new(true, -5000, 0, 5000, 110);
        router.Wallet = new(999_995_000, new Dictionary<string, ulong> { ["mint"] = 100 }, 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(100UL, Assert.Single(session.State.Positions).Tokens);
        Assert.Equal(-5000L, session.State.NetWalletChangeLamports);
        Assert.Equal(OrderStatus.Failed, Assert.Single(session.State.Orders).Status);
    }

    [Fact]
    public async Task PartialSellRetainsTheUnsoldPosition()
    {
        ConnectedSession session = await WithPositionAsync();
        await session.ExecuteAsync(new("exit", "mint", OrderSide.Sell, 100, 101, "Flatten"), Running, TestContext.Current.CancellationToken);
        router.Receipt = new(false, 5_000_000, -40, 5000, 110);
        router.Wallet = new(1_005_000_000, new Dictionary<string, ulong> { ["mint"] = 60 }, 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(60UL, Assert.Single(session.State.Positions).Tokens);
        Assert.Equal(0.006m, session.State.Positions[0].CostSol);
    }

    [Fact]
    public async Task ExpiryReleasesReserveOnlyWhenWalletAlsoProvesNoFill()
    {
        ConnectedSession session = await OpenAsync();
        await session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        router.Expired = true;
        router.Wallet = new(990_000_000, new Dictionary<string, ulong> { ["mint"] = 100 }, 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Unresolved, session.State.Orders[0].Status);
        Assert.Equal(20_000_000UL, session.State.ReservedLamports);
        router.Wallet = new(1_000_000_000, new Dictionary<string, ulong>(), 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Expired, session.State.Orders[0].Status);
        Assert.Equal(0UL, session.State.ReservedLamports);
    }

    [Fact]
    public async Task HaltArrivingDuringPreparationPreventsSubmission()
    {
        bool halted = false;
        router.AfterPrepare = () => halted = true;
        ConnectedSession session = await OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Buy(), () => new(halted), TestContext.Current.CancellationToken));
        Assert.Empty(router.Submissions);
        Assert.Empty(session.State.Orders);
    }

    [Fact]
    public async Task SimulateNeverSendsOrInventsAPosition()
    {
        ConnectedSession session = await OpenAsync("Simulate");
        await session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        Assert.Equal(OrderStatus.Simulated, Assert.Single(session.State.Orders).Status);
        Assert.Empty(session.State.Positions);
        Assert.Empty(router.Submissions);
        Assert.Equal(0UL, session.State.ReservedLamports);
    }

    [Fact]
    public async Task FeeReserveCannotBorrowFromPerpAllocationEvenWithWalletFunds()
    {
        ConnectedSession session = await OpenAsync();
        await session.SaveAsync(session.State with { NetWalletChangeLamports = -1_990_000_000 }, "Prior losses", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken));
        Assert.Empty(router.Submissions);
    }

    [Fact]
    public async Task SimulationFailureNeverJournalsASubmission()
    {
        router.ThrowOnSimulation = true;
        ConnectedSession session = await OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken));
        Assert.Empty(session.State.Orders);
        Assert.Empty(router.Submissions);
    }

    [Fact]
    public async Task StoresRejectChangedWalletIdentityAndConcurrentWriters()
    {
        ConnectedStateStore store = new(Path.Combine(directory, "connected.db"));
        ConnectedState original = await store.OpenAsync("one", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync("two", TestContext.Current.CancellationToken));
        await store.SaveAsync(original, "First writer", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(original, "Stale writer", TestContext.Current.CancellationToken));
    }

    private async Task<ConnectedSession> WithPositionAsync()
    {
        ConnectedSession session = await OpenAsync();
        router.Wallet = new(1_000_000_000, new Dictionary<string, ulong> { ["mint"] = 100 }, 50);
        await session.SaveAsync(session.State with { WalletLamports = router.Wallet.Lamports, HasReconciled = true, Positions = [new("mint", 100, 0.01m, 100, 0.01m, 100)] }, "Fixture", TestContext.Current.CancellationToken);
        return session;
    }

    [Fact]
    public void DailyHaltRemainsLatchedUntilUtcMidnightThenReleases()
    {
        PaperTradingOptions options = Options().Strategy;
        ConnectedState state = ConnectedRiskPolicy.Observe(new() { Identity = "test" }, 100, options);
        state = ConnectedRiskPolicy.Observe(state with { NetWalletChangeLamports = -150_000_000 }, 200, options);
        Assert.True(state.DailyHalt);
        Assert.True(ConnectedRiskPolicy.Observe(state with { NetWalletChangeLamports = 0 }, 300, options).DailyHalt);
        Assert.False(ConnectedRiskPolicy.Observe(state, 86400, options).DailyHalt);
    }

    [Fact]
    public async Task ExternalWalletChangeDoesNotSilentlyBecomeAnApprovedBaseline()
    {
        ConnectedSession session = await OpenAsync();
        await session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        router.Receipt = new(true, -5000, 0, 5000, 110);
        router.Wallet = router.Wallet with { Lamports = 999_995_000 };
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        router.Wallet = router.Wallet with { Lamports = 999_000_000 };
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.NotNull(session.State.ReconciliationError);
        Assert.Equal(999_995_000UL, session.State.WalletLamports);
        router.Wallet = router.Wallet with { Lamports = 999_995_000 };
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Null(session.State.ReconciliationError);
    }

    [Fact]
    public async Task ANewUnfundedSessionCanReceiveTestSolWithoutIncreasingItsAllocation()
    {
        router.Wallet = router.Wallet with { Lamports = 0 };
        ConnectedSession session = await OpenAsync();
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        router.Wallet = router.Wallet with { Lamports = 3_000_000_000 };
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.Null(session.State.ReconciliationError);
        Assert.Equal(3_000_000_000UL, session.State.WalletLamports);
        Assert.Equal(2m, session.State.EquitySol(Options().Strategy.Capital.ChainCapitalSol));
    }

    [Fact]
    public async Task ReceiptBudgetFaultRequiresAcknowledgementAndCannotBeClearedByPolling()
    {
        ConnectedSession session = await OpenAsync();
        await session.ExecuteAsync(Buy(), Running, TestContext.Current.CancellationToken);
        router.Receipt = new(false, -21_000_000, 100, 5000, 110);
        router.Wallet = new(979_000_000, new Dictionary<string, ulong> { ["mint"] = 100 }, 110);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        await session.ReconcileAsync(Running, TestContext.Current.CancellationToken);
        Assert.NotNull(session.State.ExecutionFault);
        Assert.NotNull(session.State.ReconciliationError);
        await session.AcknowledgeFaultAsync(TestContext.Current.CancellationToken);
        Assert.Null(session.State.ExecutionFault);
        Assert.Equal(100UL, Assert.Single(session.State.Positions).Tokens);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
