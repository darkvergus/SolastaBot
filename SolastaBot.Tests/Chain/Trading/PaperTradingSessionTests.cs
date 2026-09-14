using Microsoft.Data.Sqlite;
using SolastaBot.Chain.Trading;
using SolastaBot.Cli;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Host.Chain;

namespace SolastaBot.Tests.Chain.Trading;

public sealed class PaperTradingSessionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"solasta-paper-{Guid.NewGuid():N}");

    public PaperTradingSessionTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RestartRestoresThePositionAndDoesNotBuyTheSameMintAgain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PaperTradingOptions options = TradingFixture.Options;
        PaperStateStore store = new(Path.Combine(directory, "paper.db"));
        PaperTradingState state = await store.InitialiseAsync("frozen-settings", PaperTradingState.Create(100m, options), cancellationToken);
        PaperTradingSession session = new(store, new(options), state);
        await session.DiscoverAsync([TradingFixture.Launch()], new(), cancellationToken);
        await session.ObserveAsync(TradingFixture.Curve(105m), new(), cancellationToken);

        PaperTradingState restored = await store.InitialiseAsync("frozen-settings", PaperTradingState.Create(106m, options), cancellationToken);
        PaperTradingSession restarted = new(store, new(options), restored);
        await restarted.DiscoverAsync([TradingFixture.Launch(106m)], new(), cancellationToken);
        Assert.Single(restarted.State.Positions);
        Assert.Equal(8.96m, restarted.State.CashSol);

        await restarted.TickAsync(110m, new(), cancellationToken);
        await restarted.ObserveAsync(TradingFixture.Curve(111m), new(), cancellationToken);
        await restarted.DiscoverAsync([TradingFixture.Launch(112m)], new(), cancellationToken);
        Assert.Empty(restarted.State.Pending);
        Assert.Empty(restarted.State.Positions);
        IReadOnlyList<TradingEvent> events = await store.ReadEventsAsync(100, cancellationToken);
        Assert.Single(events, tradingEvent => tradingEvent.Kind == "PaperBuy");
        Assert.Single(events, tradingEvent => tradingEvent.Kind == "PaperSell");
    }

    [Fact]
    public async Task FailedCommitRollsBackBalanceSeenMintsAndEventsTogether()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PaperTradingOptions options = TradingFixture.Options;
        PaperStateStore store = new(Path.Combine(directory, "paper.db"));
        PaperTradingState initial = await store.InitialiseAsync("settings", PaperTradingState.Create(100m, options), cancellationToken);
        TradingEvent duplicate = new(1, 100m, "Test", "test-mint", "duplicate");
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveAsync(initial with { CashSol = 0m }, [duplicate, duplicate], ["test-mint"], cancellationToken));

        Assert.Equal(10m, (await store.ReadAsync(cancellationToken)).CashSol);
        Assert.Empty(await store.FindSeenAsync(["test-mint"], cancellationToken));
        Assert.Empty(await store.ReadEventsAsync(100, cancellationToken));
    }

    [Fact]
    public async Task DifferentSettingsCannotResetOrReinterpretTheExistingBalance()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PaperStateStore store = new(Path.Combine(directory, "paper.db"));
        PaperTradingState initial = PaperTradingState.Create(100m, TradingFixture.Options);
        await store.InitialiseAsync("original", initial, cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitialiseAsync("changed", initial with { CashSol = 1000m }, cancellationToken));
        Assert.Equal(10m, (await store.ReadAsync(cancellationToken)).CashSol);
    }

    [Fact]
    public async Task CliControlsPersistAndCannotPretendFlattenHasAlreadyFilled()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        PaperStateStore store = new(Path.Combine(directory, "paper.db"));
        await store.InitialiseAsync("settings", PaperTradingState.Create(100m, TradingFixture.Options), cancellationToken);

        Assert.Equal(0, await CommandLine.RunAsync(["chain", "paper", "flatten", "--state", directory]));
        Assert.Equal(new(true, true), PaperControlFiles.Read(directory));
        Assert.Equal(0, await CommandLine.RunAsync(["chain", "paper", "resume", "--state", directory]));
        Assert.Equal(new(), PaperControlFiles.Read(directory));
        Assert.Equal(0, await CommandLine.RunAsync(["chain", "paper", "status", "--state", directory]));
        Assert.Equal(0, await CommandLine.RunAsync(["chain", "paper", "events", "--state", directory]));
    }

    [Fact]
    public async Task MissingSessionIsNotCreatedByAStatusOrControlCommand()
    {
        Assert.Equal(1, await CommandLine.RunAsync(["chain", "paper", "status", "--state", directory]));
        Assert.Equal(1, await CommandLine.RunAsync(["chain", "paper", "halt", "--state", directory]));
        Assert.False(File.Exists(Path.Combine(directory, "paper.db")));
    }
}
