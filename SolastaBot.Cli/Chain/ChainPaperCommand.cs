using System.CommandLine;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;

namespace SolastaBot.Cli.Chain;

internal static class ChainPaperCommand
{
    internal static Command Build()
    {
        Command paper = new("paper", "Inspect and control the live-data paper trading worker.");
        foreach (string action in new[] { "status", "halt", "resume", "flatten", "events" })
        {
            Option<string> state = new("--state") { Description = "The worker's state directory.", Required = true };
            Command command = new(action, Description(action)) { state };
            command.SetAction(async (result, cancellationToken) =>
            {
                try
                {
                    string directory = Path.GetFullPath(result.GetRequiredValue(state));
                    PaperStateStore store = new(Path.Combine(directory, "paper.db"));
                    if (action == "status")
                    {
                        PaperTradingState session = await store.ReadAsync(cancellationToken);
                        TradingControl control = PaperControlFiles.Read(directory);
                        Console.WriteLine(FormattableString.Invariant($"PAPER | cash {session.CashSol:F8} SOL | marked equity {session.EquitySol:F8} SOL | pending {session.Pending.Count} | open {session.Positions.Count}"));
                        Console.WriteLine($"Entry halt: {control.HaltEntries}; flatten requested: {control.Flatten}; daily halt: {session.DailyHalt}");
                        Console.WriteLine($"Last worker update: {DateTimeOffset.FromUnixTimeMilliseconds((long)(session.ObservedAt * 1000m)):O}");
                        foreach (PaperPosition position in session.Positions)
                        {
                            Console.WriteLine(FormattableString.Invariant($"{position.Mint} | cost {position.CostSol:F8} SOL | mark {position.MarkSol:F8} SOL | exit {position.ExitReason ?? "not requested"} | migrated {position.Migrated}"));
                        }
                    }
                    else if (action == "events")
                    {
                        foreach (TradingEvent tradingEvent in await store.ReadEventsAsync(30, cancellationToken))
                        {
                            Console.WriteLine(FormattableString.Invariant($"{tradingEvent.Sequence} | {tradingEvent.At} | {tradingEvent.Kind} | {tradingEvent.Mint} | {tradingEvent.Reason} | {tradingEvent.AmountSol}"));
                        }
                    }
                    else
                    {
                        switch (action)
                        {
                            case "halt":
                                PaperControlFiles.Halt(directory);
                                break;
                            case "flatten":
                                PaperControlFiles.Flatten(directory);
                                break;
                            case "resume":
                                PaperControlFiles.Resume(directory);
                                break;
                        }

                        Console.WriteLine(action == "flatten"
                            ? "Paper flatten requested and entries halted. The running worker must obtain executable prices before positions close."
                            : $"Paper {action} requested. The running worker applies the control before its next decision.");
                    }

                    return 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Console.Error.WriteLine($"Paper control failed: {exception.Message}");
                    return 1;
                }
            });
            paper.Add(command);
        }

        return paper;
    }

    private static string Description(string action) => action switch
    {
        "status" => "Read the persisted paper balance and positions.",
        "events" => "Read the most recent paper trading decisions and fills.",
        "halt" => "Stop new entries while continuing to manage existing positions.",
        "resume" => "Release manual entry and flatten controls; daily risk limits still apply.",
        _ => "Request exits for all paper positions and stop new entries."
    };
}
