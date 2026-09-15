using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Markets;

public sealed class SolanaPaperWorker(UnifiedSettings settings, PortfolioStore store, PumpLaunchMarketFeed feed, ReadOnlyRateLimitedRpc rpc, Channel<string> discoveries, ILogger<SolanaPaperWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.EnableNetwork)
        {
            return;
        }

        decimal nextFeed = 0m;
        PumpRoutes routes = new(rpc);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                decimal now = Now();
                PortfolioState snapshot = store.Snapshot();
                HashSet<string> owned =
                [
                    .. snapshot.Strategies.Values.SelectMany(account => account.Positions.Select(position => position.Market).Concat(account.Orders.Select(order => order.Market)))
                ];
                TrackedMarket? market = snapshot.Markets.Values.Where(candidate => now - candidate.LastAttemptAt >= (owned.Contains(candidate.Launch.Mint) ? 3m : 8m))
                    .OrderBy(candidate => owned.Contains(candidate.Launch.Mint) ? 0 : 1).ThenBy(candidate => candidate.LastAttemptAt).FirstOrDefault();
                bool urgent = market is not null && owned.Contains(market.Launch.Mint);
                if (!urgent && now >= nextFeed)
                {
                    nextFeed = now + 20m;
                    foreach (LaunchCandidate launch in await feed.ReadLaunchesAsync(stoppingToken))
                    {
                        Admit(launch);
                    }
                }
                else if (!urgent && market is null && discoveries.Reader.TryRead(out string? discoveredMint))
                {
                    if (snapshot.Markets.Count < settings.TrackingCapacity && !snapshot.Markets.ContainsKey(discoveredMint))
                    {
                        Admit(await feed.ReadLaunchAsync(discoveredMint, stoppingToken));
                    }
                }
                else if (market is not null)
                {
                    string mint = market.Launch.Mint;
                    store.Mutate(state => state.Markets[mint].LastAttemptAt = now);
                    CurveObservation curve = await feed.ReadCurveAsync(mint, stoppingToken);
                    bool pool = curve.Complete == true;
                    decimal? fee = null;
                    if (pool)
                    {
                        RouteSnapshot route = await routes.ReadAsync(mint, SolanaPrograms.System, stoppingToken);
                        curve = new(mint, Now(), false, (decimal)route.QuoteReserve, (decimal)route.BaseReserve, (decimal)route.RealQuoteReserve, (decimal)route.RealBaseReserve, null);
                        fee = route.FeeRates.Sum(rate => (decimal)rate);
                    }
                    store.Observe(new() { Source = "solana", Market = mint, At = curve.ObservedAt, Curve = curve, Pool = pool, ProtocolFeeBasisPoints = fee });
                }
                Prune(now);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or OperationCanceledException or KeyNotFoundException or FormatException or OverflowException)
            {
                logger.LogWarning("Solana observation unavailable: {Reason}", exception.Message);
                store.Observe(new() { Source = "solana", Market = "", At = Now(), Error = exception.Message });
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            await Task.Delay(100, stoppingToken);
        }
    }

    private void Admit(LaunchCandidate launch)
    {
        PortfolioState snapshot = store.Snapshot();
        if (snapshot.Markets.ContainsKey(launch.Mint) || launch.CreatedAt <= 0m || Now() - launch.CreatedAt > 10800m)
        {
            return;
        }

        bool filtered = launch is { HasTelegram: true, HasTwitter: true, QuoteMint: SolanaPrograms.System, Curve.VirtualQuoteReserves: > 31.04m * 1_000_000_000m };
        decimal probability = filtered ? 1m : settings.ControlProbability;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(launch.Mint));
        decimal draw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(digest) / (uint.MaxValue + 1m);
        bool admitted = draw < probability && snapshot.Markets.Count < settings.TrackingCapacity;
        store.Observe(new() { Source = "solana-launch", Market = launch.Mint, At = launch.Curve.ObservedAt, Launch = launch, Curve = admitted ? launch.Curve : null, Admitted = admitted, AdmissionProbability = snapshot.Markets.Count < settings.TrackingCapacity ? probability : 0m });
    }

    private void Prune(decimal now)
    {
        store.Mutate(state =>
        {
            HashSet<string> owned = [.. state.Strategies.Values.SelectMany(account => account.Positions.Select(position => position.Market).Concat(account.Orders.Select(order => order.Market)))];
            foreach (string mint in state.Markets.Where(pair => now - pair.Value.Launch.CreatedAt > 10800m && !owned.Contains(pair.Key)).Select(pair => pair.Key).ToArray())
            {
                state.Markets.Remove(mint);
            }
        });
    }

    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
