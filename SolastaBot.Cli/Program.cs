using System.Threading.Tasks;

namespace SolastaBot.Cli;

public static class Program
{
    public static Task<int> Main(string[] args) => CommandLine.RunAsync(args);
}
