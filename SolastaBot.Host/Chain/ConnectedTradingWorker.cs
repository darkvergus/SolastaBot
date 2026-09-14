using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Execution;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using System.Text.Json;

namespace SolastaBot.Host.Chain;

public static class ConnectedTradingWorker
{
    public static async Task<int> RunAsync(string settings, string stateRoot, CancellationToken cancellationToken)
    {
        using ConnectedRuntime runtime = await ConnectedRuntime.OpenAsync(settings, stateRoot, cancellationToken);
        Console.WriteLine($"{runtime.Options.Mode} {runtime.Options.Network}; wallet={runtime.Address}; session={runtime.DirectoryPath}");
        if (runtime.Options.DevnetMints.Count > 0)
        {
            Console.WriteLine("Devnet watchlist supplies test candidates; it does not measure launch selection quality.");
        }

        ConnectedSession session = runtime.Session;
        PumpRoutes routes = new(runtime.Rpc);
        using HttpClient client = new();
        client.Timeout = TimeSpan.FromSeconds(20);
        PumpLaunchMarketFeed feed = new(client, TimeProvider.System, runtime.Options.Strategy.RequestsPerSecond);
        decimal nextFeed = 0m;
        decimal nextStatus = 0m;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await session.ReconcileAsync(runtime.Control, cancellationToken);
                decimal now = Now();
                if (!session.State.Orders.Any(order => order.Pending))
                {
                    await MarkAndExitAsync(runtime, routes, now, cancellationToken);
                    if (!session.State.Orders.Any(order => order.Pending) && now >= nextFeed)
                    {
                        nextFeed = now + runtime.Options.Strategy.FeedIntervalSeconds;
                        IReadOnlyList<LaunchCandidate> candidates = runtime.Options.DiscoverMainnetLaunches ? await feed.ReadLaunchesAsync(cancellationToken) : [];
                        foreach (LaunchCandidate candidate in candidates)
                        {
                            await EnterAsync(runtime, candidate, cancellationToken);
                            if (session.State.Orders.Any(order => order.Pending))
                            {
                                break;
                            }
                        }
                        foreach (string mint in runtime.Options.DevnetMints)
                        {
                            if (session.State.Orders.Any(order => order.Pending))
                            {
                                break;
                            }

                            if (session.State.SeenMints.Contains(mint))
                            {
                                continue;
                            }

                            RouteSnapshot route = await routes.ReadAsync(mint, runtime.Address, cancellationToken);
                            decimal observed = Now();
                            CurveObservation observation = new(mint, observed, false, (decimal)route.QuoteReserve, (decimal)route.BaseReserve, (decimal)route.RealQuoteReserve, (decimal)route.RealBaseReserve, null);
                            LaunchCandidate candidate = new(mint, observed, SolanaPrograms.System, 9, "pump", true, true, observation);
                            await EnterAsync(runtime, candidate, cancellationToken);
                        }
                    }
                }
                if (now >= nextStatus)
                {
                    nextStatus = now + 30m;
                    Console.WriteLine($"{DateTimeOffset.UtcNow:O} positions={session.State.Positions.Count} orders={session.State.Orders.Count} pending={session.State.Orders.Count(order => order.Pending)} wallet={session.State.WalletLamports / 1_000_000_000m} SOL error={session.State.ReconciliationError ?? "none"}");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {exception.GetType().Name}: {exception.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        return 0;
    }

    private static async Task EnterAsync(ConnectedRuntime runtime, LaunchCandidate candidate, CancellationToken cancellationToken)
    {
        ConnectedSession session = runtime.Session;
        if (session.State.SeenMints.Contains(candidate.Mint) || runtime.Control().HaltEntries || runtime.Control().Flatten || session.State.DailyHalt || session.State.ReconciliationError is not null)
        {
            return;
        }

        string? rejection = LaunchEntryPolicy.Rejection(candidate, Now(), runtime.Options.Strategy);
        await session.SaveAsync(session.State with { SeenMints = [.. session.State.SeenMints, candidate.Mint] }, rejection is null ? "EntrySignal" : $"RejectEntry: {rejection}", cancellationToken);
        if (rejection is not null)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds((double)runtime.Options.Strategy.Execution.EntryDelaySeconds), cancellationToken);
        if (Now() - candidate.Curve.ObservedAt > runtime.Options.Strategy.Execution.EntryDelaySeconds + runtime.Options.Strategy.Execution.MaxObservationGapSeconds)
        {
            return;
        }

        ulong amount = (ulong)decimal.Floor(runtime.Options.Strategy.Execution.TradeSizeSol * 1_000_000_000m);
        await session.ExecuteAsync(new(Guid.NewGuid().ToString("N"), candidate.Mint, OrderSide.Buy, amount, Now(), "Launch entry"), runtime.Control, cancellationToken);
    }

    private static async Task MarkAndExitAsync(ConnectedRuntime runtime, PumpRoutes routes, decimal now, CancellationToken cancellationToken)
    {
        ConnectedSession session = runtime.Session;
        List<ConfirmedPosition> positions = [];
        foreach (ConfirmedPosition position in session.State.Positions)
        {
            try
            {
                RouteSnapshot route = await routes.ReadAsync(position.Mint, runtime.Address, cancellationToken);
                ulong minimumOutput = route.SellOutput(position.Tokens, runtime.Options.SlippageBasisPoints);
                JsonElement block = await runtime.Rpc.CallAsync("getLatestBlockhash", [new { commitment = "confirmed" }], cancellationToken);
                TransactionBuilder message = new TransactionBuilder().SetFeePayer(new PublicKey(runtime.Address)).SetRecentBlockHash(block.GetProperty("value").GetProperty("blockhash").GetString()!);
                foreach (TransactionInstruction instruction in routes.Instructions(route, new("quote", position.Mint, OrderSide.Sell, position.Tokens, now, "Exit quote"), runtime.Address, minimumOutput))
                {
                    message.AddInstruction(instruction);
                }

                JsonElement fee = await runtime.Rpc.CallAsync("getFeeForMessage", [Convert.ToBase64String(message.CompileMessage()), new { commitment = "confirmed" }], cancellationToken);
                if (fee.GetProperty("value").ValueKind == JsonValueKind.Null)
                {
                    throw new InvalidDataException("Exit network fee unavailable.");
                }

                decimal proceeds = Math.Max(0m, (minimumOutput - (decimal)fee.GetProperty("value").GetUInt64()) / 1_000_000_000m);
                positions.Add(position with { MarkSol = proceeds, MarkedAt = now });
            }
            catch (InvalidDataException exception)
            {
                Console.Error.WriteLine($"Quote unavailable for {position.Mint}: {exception.Message}; retaining holdings.");
                positions.Add(position);
            }
        }
        ConnectedState marked = session.State with { Positions = positions };
        marked = ConnectedRiskPolicy.Observe(marked, now, runtime.Options.Strategy);

        positions =
        [
            .. marked.Positions
                .Select(position => new
                {
                    position,
                    reason = position.ExitReason ?? PositionExitPolicy.Reason(position.CostSol, position.MarkSol, position.EnteredAt, now, runtime.Options.Strategy,
                        runtime.Control(), marked.DailyHalt)
                })
                .Select(decision => decision.reason is null ? decision.position : decision.position with { ExitRequestedAt = decision.position.ExitRequestedAt ?? now, ExitReason = decision.reason })

        ];

        await session.SaveAsync(marked with { Positions = positions }, "Decision", cancellationToken);
        ConfirmedPosition? exit = positions.FirstOrDefault(position => position.ExitRequestedAt.HasValue && now > position.ExitRequestedAt && now >= position.ExitRequestedAt + runtime.Options.Strategy.Execution.ExitDelaySeconds);
        if (exit is not null)
        {
            await session.ExecuteAsync(new(Guid.NewGuid().ToString("N"), exit.Mint, OrderSide.Sell, exit.Tokens, now, exit.ExitReason!), runtime.Control, cancellationToken);
        }
    }

    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
