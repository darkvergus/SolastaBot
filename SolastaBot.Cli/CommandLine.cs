using System.CommandLine;
using SolastaBot.Cli.Chain;

namespace SolastaBot.Cli;

public static class CommandLine
{
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        RootCommand root = new("SolastaBot: perpetual futures and Solana launch research tools.")
        {
            DataCommands.Build(),
            BacktestCommand.Build(),
            WalkForwardCommand.Build(),
            ChainReplayCommand.Build()
        };

        return root.Parse(args).InvokeAsync();
    }
}
