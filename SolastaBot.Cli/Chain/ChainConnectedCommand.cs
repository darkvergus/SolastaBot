using System.CommandLine;
using System.Text.Json;
using SolastaBot.Chain.Execution;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Exchange.Chain.Solana.Protocol;

namespace SolastaBot.Cli.Chain;

internal static class ChainConnectedCommand
{
    internal static Command Build()
    {
        Command connected = new("connected", "Solana simulation and devnet execution; mainnet sending is disabled.");
        Option<string> walletPath = new("--path") { Required = true, Description = "New keypair path outside the repository." };
        Command wallet = new("wallet-create", "Create a protected dedicated bot keypair.") { walletPath };
        wallet.SetAction(result =>
        {
            try
            {
                Console.WriteLine($"Wallet public key: {FileWalletSigner.Create(result.GetRequiredValue(walletPath))}");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        });
        connected.Add(wallet);
        foreach (string action in new[] { "status", "events", "halt", "resume", "flatten" })
        {
            Option<string> directory = new("--state") { Required = true, Description = "Exact session directory printed by the worker." };
            Command control = new(action, $"Connected session {action}.") { directory };
            control.SetAction(async (result, cancellationToken) =>
            {
                try
                {
                    string path = Path.GetFullPath(result.GetRequiredValue(directory));
                    if (!File.Exists(Path.Combine(path, "connected.db")))
                    {
                        throw new IOException("No connected session exists in this directory.");
                    }

                    if (action is "status" or "events")
                    {
                        ConnectedStateStore store = new(Path.Combine(path, "connected.db"));
                        string payload = await store.ReadAsync(action == "events", cancellationToken);
                        if (action == "status")
                        {
                            ConnectedState state = JsonSerializer.Deserialize<ConnectedState>(payload)!;
                            Console.WriteLine($"Wallet {state.WalletLamports / 1_000_000_000m} SOL; positions {state.Positions.Count}; reserved {state.ReservedLamports / 1_000_000_000m} SOL; daily halt {state.DailyHalt}");
                            Console.WriteLine($"Reconciliation: {state.ReconciliationError ?? "clear"}");
                            foreach (ConfirmedPosition position in state.Positions)
                            {
                                Console.WriteLine($"{position.Mint}: {position.Tokens} base units; cost {position.CostSol} SOL; exit {position.ExitReason ?? "none"}");
                            }

                            foreach (ExecutionOrder order in state.Orders.TakeLast(10))
                            {
                                Console.WriteLine($"{order.Intent.Side} {order.Intent.Mint}: {order.Status}; {order.Signature}; {order.Detail}");
                            }
                        }
                        else
                        {
                            Console.WriteLine(payload);
                        }
                    }
                    else
                    {
                        if (action == "resume")
                        {
                            File.Delete(Path.Combine(path, "FLATTEN"));
                            File.Delete(Path.Combine(path, "HALT"));
                        }
                        else
                        {
                            await File.WriteAllTextAsync(Path.Combine(path, "HALT"), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
                            if (action == "flatten")
                            {
                                await File.WriteAllTextAsync(Path.Combine(path, "FLATTEN"), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
                            }
                        }
                        Console.WriteLine($"{action} requested. The worker applies controls before submission; submitted transactions still require reconciliation.");
                    }
                    return 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Console.Error.WriteLine(exception.Message);
                    return 1;
                }
            });
            connected.Add(control);
        }
        foreach (string action in new[] { "check", "quote", "buy", "sell", "reconcile", "airdrop", "acknowledge-fault" })
        {
            Option<string> settings = new("--settings") { Required = true };
            Option<string> stateRoot = new("--state") { Required = true, Description = "Root directory containing network and wallet sessions." };
            Option<string> mint = new("--mint") { Required = action is "quote" or "buy" or "sell" };
            Option<bool> confirm = new("--confirm") { Description = "Acknowledge a reviewed execution fault only when the wallet and journal agree." };
            Command command = new(action, $"Run {action} through the configured router. Buy and sell send only in Devnet mode.") { settings, stateRoot, mint, confirm };
            command.SetAction(async (result, cancellationToken) =>
            {
                try
                {
                    using ConnectedRuntime runtime = await ConnectedRuntime.OpenAsync(result.GetRequiredValue(settings), result.GetRequiredValue(stateRoot), cancellationToken);
                    Console.WriteLine($"{runtime.Options.Mode} {runtime.Options.Network}; wallet={runtime.Address}; session={runtime.DirectoryPath}");
                    if (action == "acknowledge-fault")
                    {
                        if (!result.GetValue(confirm))
                        {
                            throw new ArgumentException("Review the recorded fault, then pass --confirm to acknowledge it.");
                        }

                        await runtime.Session.AcknowledgeFaultAsync(cancellationToken);
                        Console.WriteLine("Fault acknowledged; wallet balances and holdings were verified without adjustment.");
                        return 0;
                    }
                    if (action == "airdrop")
                    {
                        if (runtime.Session.State.Orders.Count > 0 || runtime.Session.State.Positions.Count > 0)
                        {
                            throw new InvalidOperationException("Fund a new test session before trading.");
                        }

                        JsonElement signature = await runtime.Rpc.CallAsync("requestAirdrop", [runtime.Address, 1_000_000_000UL], cancellationToken);
                        Console.WriteLine($"Devnet faucet signature: {signature.GetString()}. Wait for finalization before check or trade.");
                        return 0;
                    }
                    if (action == "quote")
                    {
                        PumpRoutes routes = new(runtime.Rpc);
                        RouteSnapshot quote = await routes.ReadAsync(result.GetRequiredValue(mint), runtime.Address, cancellationToken);
                        ulong input = (ulong)(runtime.Options.Strategy.Execution.TradeSizeSol * 1_000_000_000m);
                        Console.WriteLine($"{quote.Route} slot={quote.Slot}; buy {input} lamports; minimum tokens={quote.BuyOutput(input, runtime.Options.SlippageBasisPoints)}; fee rates={string.Join(",", quote.FeeRates)} bps");
                        Console.WriteLine($"Curve={SolanaPrograms.Curve(quote.Mint)}; canonical pool={SolanaPrograms.Pool(quote.Mint)}");
                        return 0;
                    }
                    await runtime.Session.ReconcileAsync(runtime.Control, cancellationToken);
                    if (action is "check" or "reconcile")
                    {
                        Console.WriteLine($"Wallet {runtime.Session.State.WalletLamports / 1_000_000_000m} SOL; pending={runtime.Session.State.Orders.Count(order => order.Pending)}; reconciliation={runtime.Session.State.ReconciliationError ?? "clear"}");
                        return runtime.Session.State.ReconciliationError is null ? 0 : 1;
                    }
                    string token = result.GetRequiredValue(mint);
                    OrderSide side = action == "buy" ? OrderSide.Buy : OrderSide.Sell;
                    ulong amount = side == OrderSide.Buy ? (ulong)(runtime.Options.Strategy.Execution.TradeSizeSol * 1_000_000_000m) :
                        runtime.Session.State.Positions.Single(position => position.Mint == token).Tokens;
                    await runtime.Session.ExecuteAsync(new(Guid.NewGuid().ToString("N"), token, side, amount, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m, "Explicit CLI devnet exercise"), runtime.Control, cancellationToken);
                    ExecutionOrder order = runtime.Session.State.Orders[^1];
                    Console.WriteLine($"{order.Status}; signature={order.Signature}; {order.Detail}");
                    return 0;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or HttpRequestException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Console.Error.WriteLine(exception.Message);
                    return 1;
                }
            });
            connected.Add(command);
        }
        return connected;
    }
}
