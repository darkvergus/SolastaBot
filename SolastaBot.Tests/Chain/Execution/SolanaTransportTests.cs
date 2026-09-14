using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using Solnet.Rpc.Models;
using Solnet.Wallet;

namespace SolastaBot.Tests.Chain.Execution;

public sealed class SolanaTransportTests
{
    [Fact]
    public async Task MainnetGenesisPreventsSendEvenWhenDevnetWasConfigured()
    {
        using RpcTestHandler handler = new((method, parameters) => "5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp");
        using HttpClient client = new(handler);
        SolanaRpc rpc = new(client, new Uri("https://rpc.test"), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.SendAsync("signed", 1, () => true, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "getGenesisHash" }, handler.Methods);
    }

    [Fact]
    public async Task HaltDuringNetworkCheckPreventsTheFollowingHttpSubmission()
    {
        bool allowed = true;
        using RpcTestHandler handler = new((method, parameters) => { allowed = false; return SolanaRpc.DevnetGenesis; });
        using HttpClient client = new(handler);
        SolanaRpc rpc = new(client, new Uri("https://rpc.test"), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.SendAsync("signed", 1, () => allowed, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("sendTransaction", handler.Methods);
    }

    [Fact]
    public async Task SimulateTransportAndGenericRpcCannotBypassSendGuard()
    {
        using RpcTestHandler handler = new((method, parameters) => SolanaRpc.DevnetGenesis);
        using HttpClient client = new(handler);
        SolanaRpc rpc = new(client, new Uri("https://rpc.test"), false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.SendAsync("signed", 1, () => true, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rpc.CallAsync("sendTransaction", ["signed"], TestContext.Current.CancellationToken));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public void ProtectedWalletCanReloadAndSignAVerifiableTransaction()
    {
        string path = Path.Combine(Path.GetTempPath(), $"solasta-test-wallet-{Guid.NewGuid():N}.json");
        try
        {
            string address = FileWalletSigner.Create(path);
            FileWalletSigner signer = new(path);
            Assert.Equal(address, signer.Address);
            Account destination = new();
            byte[] signed = signer.Sign(new PublicKey(new byte[32]).Key, [TokenInstructions.Transfer(address, destination.PublicKey.Key, 100)]);
            Assert.Equal(1, signed[0]);
            Assert.True(new PublicKey(address).Verify(signed[65..], signed[1..65]));
            Transaction decoded = Transaction.Deserialize(signed);
            Assert.NotNull(decoded);
            Assert.Throws<IOException>(() => FileWalletSigner.Create(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWalletCannotRunInTwoProcessesOrSwitchToAnEmptyJournal()
    {
        string address = new Account().PublicKey.Key;
        string firstPath = Path.Combine(Path.GetTempPath(), $"first-{Guid.NewGuid():N}");
        string secondPath = Path.Combine(Path.GetTempPath(), $"second-{Guid.NewGuid():N}");
        string binding = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SolastaBot", "sessions", $"Devnet-Devnet-{address}.session");
        try
        {
            using (WalletSessionLease first = new(address, "Devnet", "Devnet", firstPath))
            {
                Assert.Throws<IOException>(() => new WalletSessionLease(address, "Devnet", "Devnet", firstPath));
            }
            Assert.Throws<InvalidOperationException>(() => new WalletSessionLease(address, "Devnet", "Devnet", secondPath));
            using WalletSessionLease resumed = new(address, "Devnet", "Devnet", firstPath);
        }
        finally
        {
            File.Delete(binding);
        }
    }
}
