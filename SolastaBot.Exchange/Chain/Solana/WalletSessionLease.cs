using System.Text;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class WalletSessionLease : IDisposable
{
    private readonly FileStream stream;

    public WalletSessionLease(string address, string network, string mode, string sessionDirectory)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolastaBot", "sessions");
        Directory.CreateDirectory(directory);
        stream = new(Path.Combine(directory, $"{network}-{mode}-{address}.session"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            string existing = reader.ReadToEnd();
            if (existing.Length > 0 && !string.Equals(existing, Path.GetFullPath(sessionDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"This wallet is already bound to {existing}. Reuse its journal to preserve pending transactions and allocation history.");
            }

            if (existing.Length == 0)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(sessionDirectory));
                stream.Write(bytes);
                stream.Flush(true);
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => stream.Dispose();
}
