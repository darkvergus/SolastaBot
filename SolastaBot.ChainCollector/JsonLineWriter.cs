using System.Text;
using System.Text.Json;

namespace SolastaBot.ChainCollector;

public sealed class JsonLineWriter : IDisposable
{
    private readonly object syncRoot = new();
    private readonly StreamWriter writer;

    public JsonLineWriter(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A path is required.", nameof(path));
        }

        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

        writer = new(stream, new UTF8Encoding(false));
    }

    public void Write(IReadOnlyDictionary<string, object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);

        string line = JsonSerializer.Serialize(row);

        lock (syncRoot)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }

    public void Dispose()
    {
        writer.Dispose();
    }
}