using SolastaBot.Chain.Execution;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Host.Chain;
using System.Text.Json;

namespace SolastaBot.Tests.Chain.Execution;

public sealed class DevnetRoundTripTests
{
    [Fact]
    public async Task OptInEmptyWatchlistWorkerStopsCleanlyAndReleasesItsWalletLease()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOLASTA_DEVNET_TEST") == "1", "Opt-in read-only devnet worker check.");
        string settings = Environment.GetEnvironmentVariable("SOLASTA_DEVNET_SETTINGS") ?? throw new InvalidOperationException("SOLASTA_DEVNET_SETTINGS is required.");
        string stateRoot = Environment.GetEnvironmentVariable("SOLASTA_DEVNET_STATE") ?? throw new InvalidOperationException("SOLASTA_DEVNET_STATE is required.");
        ConnectedOptions options = JsonSerializer.Deserialize<ConnectedOptions>(await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))!;
        Assert.SkipUnless(options.DevnetMints.Count == 0 && !options.DiscoverMainnetLaunches, "Read-only worker check requires an empty watchlist and disabled discovery.");
        long revision;
        int orders;
        using (ConnectedRuntime before = await ConnectedRuntime.OpenAsync(settings, stateRoot, TestContext.Current.CancellationToken))
        {
            Assert.Empty(before.Session.State.Positions);
            Assert.DoesNotContain(before.Session.State.Orders, order => order.Pending);
            revision = before.Session.State.Revision;
            orders = before.Session.State.Orders.Count;
        }
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await ConnectedTradingWorker.RunAsync(settings, stateRoot, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        using ConnectedRuntime after = await ConnectedRuntime.OpenAsync(settings, stateRoot, TestContext.Current.CancellationToken);
        Assert.True(after.Session.State.Revision > revision);
        Assert.Equal(orders, after.Session.State.Orders.Count);
        Assert.Empty(after.Session.State.Positions);
    }

    [Fact]
    public async Task OptInBuyThenSellReconcilesActualDevnetHoldingsAndWalletChange()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("SOLASTA_DEVNET_TEST") == "1", "Set SOLASTA_DEVNET_TEST=1 with dedicated devnet settings, state root and mint to run the funded exercise.");
        string settings = Environment.GetEnvironmentVariable("SOLASTA_DEVNET_SETTINGS") ?? throw new InvalidOperationException("SOLASTA_DEVNET_SETTINGS is required.");
        string stateRoot = Environment.GetEnvironmentVariable("SOLASTA_DEVNET_STATE") ?? throw new InvalidOperationException("SOLASTA_DEVNET_STATE is required.");
        string mint = Environment.GetEnvironmentVariable("SOLASTA_DEVNET_MINT") ?? throw new InvalidOperationException("SOLASTA_DEVNET_MINT is required.");
        using ConnectedRuntime runtime = await ConnectedRuntime.OpenAsync(settings, stateRoot, TestContext.Current.CancellationToken);
        Assert.Equal("Devnet", runtime.Options.Mode);
        await runtime.Session.ReconcileAsync(runtime.Control, TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Session.State.Positions);
        Assert.DoesNotContain(runtime.Session.State.Orders, order => order.Pending);
        Assert.Null(runtime.Session.State.ReconciliationError);
        ulong amount = checked((ulong)(runtime.Options.Strategy.Execution.TradeSizeSol * 1_000_000_000m));
        Assert.SkipWhen(runtime.Session.State.WalletLamports < amount + 2 * runtime.Options.FeeReserveLamports, "Dedicated devnet wallet has insufficient test SOL; no transaction was sent. Fund it using the devnet faucet.");
        ulong startingWallet = runtime.Session.State.WalletLamports;
        long startingJournal = runtime.Session.State.NetWalletChangeLamports;
        await runtime.Session.ExecuteAsync(new(Guid.NewGuid().ToString("N"), mint, OrderSide.Buy, amount, Now(), "Opt-in devnet buy"), runtime.Control, TestContext.Current.CancellationToken);
        await ConfirmAsync(runtime);
        ConfirmedPosition position = Assert.Single(runtime.Session.State.Positions);
        Assert.True(position.Tokens > 0);
        Assert.Equal(OrderStatus.Confirmed, runtime.Session.State.Orders[^1].Status);
        await runtime.Session.ExecuteAsync(new(Guid.NewGuid().ToString("N"), mint, OrderSide.Sell, position.Tokens, Now(), "Opt-in devnet sell"), runtime.Control, TestContext.Current.CancellationToken);
        await ConfirmAsync(runtime);
        Assert.Equal(OrderStatus.Confirmed, runtime.Session.State.Orders[^1].Status);
        Assert.Empty(runtime.Session.State.Positions);
        Assert.Null(runtime.Session.State.ReconciliationError);
        Assert.Equal((decimal)runtime.Session.State.WalletLamports - startingWallet, runtime.Session.State.NetWalletChangeLamports - (decimal)startingJournal);
    }

    private static async Task ConfirmAsync(ConnectedRuntime runtime)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (runtime.Session.State.Orders.Any(order => order.Pending) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await runtime.Session.ReconcileAsync(runtime.Control, TestContext.Current.CancellationToken);
        }
        Assert.DoesNotContain(runtime.Session.State.Orders, order => order.Pending);
    }

    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
