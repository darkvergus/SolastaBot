using System.Text.Json;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Host.Chain;

public sealed class LaunchTradingWorker(PaperWorkerSettings settings, ILaunchMarketFeed feed, TimeProvider timeProvider,
    ILogger<LaunchTradingWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Directory.CreateDirectory(settings.StateDirectory);
            await using FileStream sessionLock = new(Path.Combine(settings.StateDirectory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            PaperStateStore store = new(Path.Combine(settings.StateDirectory, "paper.db"));
            PaperTradingState state = await store.InitialiseAsync(settings.SettingsHash, PaperTradingState.Create(Now(), settings.Trading), stoppingToken);
            PaperTradingSession session = new(store, new(settings.Trading), state);
            decimal nextFeedAt = 0m;
            decimal nextStatusAt = 0m;
            Dictionary<string, decimal> attemptedAt = new(StringComparer.Ordinal);
            logger.LogInformation("PAPER trading started. Chain cash {CashSol} SOL; reserved perp allocation {PerpReserveSol} SOL; positions restored {Positions}.",
                state.CashSol, settings.Trading.Capital.PerpReserveSol, state.Positions.Count);

            while (!stoppingToken.IsCancellationRequested)
            {
                decimal now = Now();
                Log(await session.TickAsync(now, PaperControlFiles.Read(settings.StateDirectory), stoppingToken));
                try
                {
                    if (now >= nextFeedAt)
                    {
                        nextFeedAt = now + settings.Trading.FeedIntervalSeconds;
                        IReadOnlyList<LaunchCandidate> launches = await feed.ReadLaunchesAsync(stoppingToken);
                        Log(await session.DiscoverAsync(launches, PaperControlFiles.Read(settings.StateDirectory), stoppingToken));
                    }
                    else
                    {
                        string? mint = NextMint(session.State, now, attemptedAt);
                        if (mint is not null)
                        {
                            attemptedAt[mint] = now;
                            CurveObservation observation = await feed.ReadCurveAsync(mint, stoppingToken);
                            Log(await session.ObserveAsync(observation, PaperControlFiles.Read(settings.StateDirectory), stoppingToken));
                        }
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException || exception is OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Market observation unavailable: {Reason}. No fill was assumed.", exception.Message);
                }

                HashSet<string> tracked = session.State.Positions.Select(position => position.Mint).Concat(session.State.Pending.Select(entry => entry.Mint)).ToHashSet(StringComparer.Ordinal);
                foreach (string completed in attemptedAt.Keys.Where(mint => !tracked.Contains(mint)).ToArray())
                {
                    attemptedAt.Remove(completed);
                }

                if (now >= nextStatusAt)
                {
                    nextStatusAt = now + 30m;
                    logger.LogInformation("PAPER cash {CashSol} SOL; marked equity {EquitySol} SOL; pending {Pending}; positions {Positions}; daily halt {DailyHalt}.",
                        session.State.CashSol, session.State.EquitySol, session.State.Pending.Count, session.State.Positions.Count, session.State.DailyHalt);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Paper worker stopped. Persisted positions remain available for restart.");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "Paper worker stopped after a failure.");
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private string? NextMint(PaperTradingState state, decimal now, IReadOnlyDictionary<string, decimal> attemptedAt)
    {
        IEnumerable<string> candidates = state.Positions.Where(position => !position.Migrated).Select(position => position.Mint)
            .Concat(state.Pending.Where(entry => now >= entry.ExecuteAfter).Select(entry => entry.Mint));
        return candidates.OrderBy(mint => attemptedAt.GetValueOrDefault(mint, 0m)).ThenBy(mint => mint, StringComparer.Ordinal)
            .FirstOrDefault(mint => now - attemptedAt.GetValueOrDefault(mint, 0m) >= settings.Trading.PositionPollSeconds);
    }

    private decimal Now() => timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000m;

    private void Log(IReadOnlyList<TradingEvent> events)
    {
        foreach (TradingEvent tradingEvent in events.Where(tradingEvent => tradingEvent.Kind != "RejectEntry"))
        {
            logger.LogInformation("{Kind} {Mint}: {Reason}; amount {AmountSol} SOL; event {Sequence}.", tradingEvent.Kind, tradingEvent.Mint, tradingEvent.Reason, tradingEvent.AmountSol, tradingEvent.Sequence);
        }
    }
}
