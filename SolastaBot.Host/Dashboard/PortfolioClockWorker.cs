using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Dashboard;

public sealed class PortfolioClockWorker(PortfolioStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            store.Mutate(state => store.Engine.Tick(state, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m));
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
