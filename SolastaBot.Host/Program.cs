namespace SolastaBot.Host;

public static class Program
{
    public static Task Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        Console.Error.WriteLine("solasta host: not wired up yet.");
        return Task.CompletedTask;
    }
}
