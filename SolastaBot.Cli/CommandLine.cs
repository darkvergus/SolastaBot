using System.CommandLine;

namespace SolastaBot.Cli;

public static class CommandLine
{
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        RootCommand root = new("SolastaBot: perpetual futures research and trading tools.")
        {
            DataCommands.Build(),
            BacktestCommand.Build(),
            WalkForwardCommand.Build()
        };

        return root.Parse(args).InvokeAsync();
    }
}
