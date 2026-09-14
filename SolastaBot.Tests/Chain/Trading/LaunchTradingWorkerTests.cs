using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;
using SolastaBot.Host.Chain;

namespace SolastaBot.Tests.Chain.Trading;

public sealed class LaunchTradingWorkerTests
{
    [Fact]
    public async Task HostedWorkerDiscoversBuysMonitorsAndSellsWithoutNetworkOrWalletAccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), $"solasta-worker-{Guid.NewGuid():N}");
        PaperTradingOptions options = TradingFixture.Options with
        {
            Execution = ChainFixture.Options with { EntryDelaySeconds = 0m, ExitDelaySeconds = 0m, HoldSeconds = 0.1m },
            PositionPollSeconds = 0.01m
        };
        StubLaunchMarketFeed feed = new();
        using IHost host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(new PaperWorkerSettings(directory, "test-settings", options));
            services.AddSingleton<ILaunchMarketFeed>(feed);
            services.AddSingleton(TimeProvider.System);
            services.AddHostedService<LaunchTradingWorker>();
        }).Build();

        try
        {
            await host.StartAsync(cancellationToken);
            PaperStateStore store = new(Path.Combine(directory, "paper.db"));
            IReadOnlyList<TradingEvent> events = [];
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                if (!File.Exists(Path.Combine(directory, "paper.db")))
                {
                    continue;
                }

                events = await store.ReadEventsAsync(100, cancellationToken);
                if (events.Any(tradingEvent => tradingEvent.Kind == "PaperSell"))
                {
                    break;
                }
            }

            Assert.Single(events, tradingEvent => tradingEvent.Kind == "PaperBuy");
            Assert.Single(events, tradingEvent => tradingEvent.Kind == "PaperSell");
            Assert.True(feed.LaunchReads >= 1);
            Assert.True(feed.CurveReads >= 2);
            Assert.Empty((await store.ReadAsync(cancellationToken)).Positions);
        }
        finally
        {
            await host.StopAsync(cancellationToken);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
