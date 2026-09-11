using System.CommandLine;

namespace SolastaBot.Cli;

public static class CommandLine
{
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        RootCommand root = new("SolastaBot: perpetual futures research and trading tools.");
        root.Add(DataCommands.Build());
        root.Add(BacktestCommand.Build());
        root.Add(WalkForwardCommand.Build());

        return root.Parse(args).InvokeAsync();
    }
}
