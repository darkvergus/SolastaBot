using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolastaBot.Chain.Execution;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class ConnectedRuntime : IDisposable
{
    private readonly FileStream lease;
    private readonly HttpClient client;
    private readonly WalletSessionLease walletLease;
    public ConnectedOptions Options { get; }
    public string DirectoryPath { get; }
    public string Address { get; }
    public ISolanaRpc Rpc { get; }
    public ConnectedSession Session { get; }

    private ConnectedRuntime(ConnectedOptions options, string directory, string address, FileStream lease, HttpClient client, ISolanaRpc rpc, ConnectedSession session, WalletSessionLease walletLease)
    {
        Options = options;
        DirectoryPath = directory;
        Address = address;
        this.lease = lease;
        this.client = client;
        this.walletLease = walletLease;
        Rpc = rpc;
        Session = session;
    }

    public static async Task<ConnectedRuntime> OpenAsync(string settings, string root, CancellationToken cancellationToken)
    {
        JsonSerializerOptions json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        ConnectedOptions options = JsonSerializer.Deserialize<ConnectedOptions>(await File.ReadAllTextAsync(settings, cancellationToken), json) ?? throw new InvalidDataException("Missing execution settings.");
        options.Validate();
        FileWalletSigner signer = new(options.WalletPath);
        string directory = Path.Combine(Path.GetFullPath(root), $"{options.Mode.ToLowerInvariant()}-{options.Network.ToLowerInvariant()}-{signer.Address}");
        Directory.CreateDirectory(directory);
        FileStream lease = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        HttpClient client = new();
        WalletSessionLease? walletLease = null;
        try
        {
            walletLease = new(signer.Address, options.Network, options.Mode, directory);
            ISolanaRpc rpc = new SolanaRpc(client, options.RpcUrl, options.Mode == "Devnet");
            string? genesis = (await rpc.CallAsync("getGenesisHash", [], cancellationToken)).GetString();
            string expected = options.Network == "Devnet" ? SolanaRpc.DevnetGenesis : "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp";
            if (genesis != expected)
            {
                throw new InvalidOperationException("RPC genesis does not match the configured network.");
            }

            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(options))));
            ConnectedStateStore store = new(Path.Combine(directory, "connected.db"));
            ConnectedState state = await store.OpenAsync($"{genesis}:{signer.Address}:{options.Mode}:{hash}", cancellationToken);
            ConnectedSession session = new(store, new SolanaOrderRouter(rpc, signer, options), options, state);
            return new(options, directory, signer.Address, lease, client, rpc, session, walletLease);
        }
        catch
        {
            lease.Dispose();
            client.Dispose();
            walletLease?.Dispose();
            throw;
        }
    }

    public TradingControl Control() => new(Exists("HALT"), Exists("FLATTEN"));

    public void Dispose()
    {
        lease.Dispose();
        client.Dispose();
        walletLease.Dispose();
    }

    private bool Exists(string name)
    {
        try
        {
            File.GetAttributes(Path.Combine(DirectoryPath, name));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }
}
