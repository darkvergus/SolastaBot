using SolastaBot.Chain.Trading;

namespace SolastaBot.Data.Chain.Trading;

public static class PaperControlFiles
{
    public static TradingControl Read(string directory) => new(Exists(Path.Combine(directory, "HALT")), Exists(Path.Combine(directory, "FLATTEN")));

    public static void Halt(string directory)
    {
        RequireSession(directory);
        File.WriteAllText(Path.Combine(directory, "HALT"), "Entries halted");
    }

    public static void Flatten(string directory)
    {
        RequireSession(directory);
        File.WriteAllText(Path.Combine(directory, "HALT"), "Entries halted");
        File.WriteAllText(Path.Combine(directory, "FLATTEN"), "Close positions when executable prices are available");
    }

    public static void Resume(string directory)
    {
        RequireSession(directory);
        File.Delete(Path.Combine(directory, "FLATTEN"));
        File.Delete(Path.Combine(directory, "HALT"));
    }

    private static void RequireSession(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "paper.db")))
        {
            throw new DirectoryNotFoundException("No paper session exists in this state directory.");
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
