using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SolastaBot.Chain.Execution;
using SolastaBot.Exchange.Chain.Solana;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Solnet.Wallet.Utilities;

namespace SolastaBot.Tests.Chain.Execution;

public sealed class PumpProtocolTests
{
    [Theory, InlineData("pump-sell.json", "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P", 2), InlineData("pump-swap.json", "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA", 3)]
    public async Task BothRoutesProduceVerifiableTransactionsWithinSolanasPacketLimit(string fixture, string program, int mintIndex)
    {
        string path = Path.Combine(Path.GetTempPath(), $"solasta-route-wallet-{Guid.NewGuid():N}.json");

        try
        {
            FileWalletSigner.Create(path);
            FileWalletSigner signer = new(path);
            FixtureSolanaRpc rpc = new(fixture);
            string mint = Instruction(rpc, program).GetProperty("accounts")[mintIndex].GetString()!;
            SolanaOrderRouter router = new(rpc, signer, ConnectedSessionTests.Options());
            ExecutionOrder prepared = await router.PrepareAsync(new("signed", mint, OrderSide.Buy, 10_000_000, 100, "Test"), TestContext.Current.CancellationToken);
            byte[] transaction = Convert.FromBase64String(prepared.Transaction);
            Assert.InRange(transaction.Length, 100, 1232);
            Assert.True(new PublicKey(signer.Address).Verify(transaction[65..], transaction[1..65]));
            Assert.Equal(prepared.Signature, Encoders.Base58.EncodeData(transaction, 1, 64));
            Assert.NotNull(prepared.Quote);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SimulationNeverGivesTheRpcAUsableWalletSignature()
    {
        FixtureSolanaRpc rpc = new("pump-sell.json");
        NonSigningWallet wallet = new();
        string mint = Instruction(rpc, SolanaPrograms.Pump).GetProperty("accounts")[2].GetString()!;
        SolanaOrderRouter router = new(rpc, wallet, ConnectedSessionTests.Options("Simulate"));
        ExecutionOrder prepared = await router.PrepareAsync(new("simulate", mint, OrderSide.Buy, 10_000_000, 100, "Test"), TestContext.Current.CancellationToken);
        byte[] transaction = Convert.FromBase64String(prepared.Transaction);
        Assert.Equal(new byte[64], transaction[1..65]);
        Assert.False(new PublicKey(wallet.Address).Verify(transaction[65..], transaction[1..65]));
    }

    [Fact]
    public async Task PumpSellMatchesActualDevnetInstructionIncludingUpgradeAccounts()
    {
        FixtureSolanaRpc rpc = new("pump-sell.json");
        JsonElement recorded = Instruction(rpc, SolanaPrograms.Pump);
        string[] keys = [.. recorded.GetProperty("accounts").EnumerateArray().Select(key => key.GetString()!)];
        byte[] data = Encoders.Base58.DecodeData(recorded.GetProperty("data").GetString()!);
        PumpRoutes routes = new(rpc);
        RouteSnapshot route = await routes.ReadAsync(keys[2], keys[6], TestContext.Current.CancellationToken);
        Dictionary<string, string> accounts = new(route.Accounts) { ["fee_recipient"] = keys[1] };
        route = route with { Accounts = accounts, BuybackRecipient = keys[^1] };
        OrderIntent intent = new("fixture", keys[2], OrderSide.Sell, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8)), 0, "Fixture");
        TransactionInstruction actual = Assert.Single(routes.Instructions(route, intent, keys[6], BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16))));
        Assert.Equal(data, actual.Data);
        Assert.Equal(keys, actual.Keys.Select(key => key.PublicKey));
        Assert.True(actual.Keys[6].IsSigner);
        Assert.True(actual.Keys[^1].IsWritable);
        Assert.False(actual.Keys[^2].IsWritable);
        Assert.Equal("Pump", route.Route);
    }

    [Fact]
    public async Task CompletedCurveResolvesCanonicalPoolAndBuildsBoundedSwapWithUnwrap()
    {
        FixtureSolanaRpc rpc = new("pump-swap.json");
        JsonElement recorded = Instruction(rpc, SolanaPrograms.Amm);
        string[] keys = [.. recorded.GetProperty("accounts").EnumerateArray().Select(key => key.GetString()!)];
        PumpRoutes routes = new(rpc);
        RouteSnapshot route = await routes.ReadAsync(keys[3], keys[1], TestContext.Current.CancellationToken);
        Assert.Equal("PumpSwap", route.Route);
        Assert.Equal(keys[0], SolanaPrograms.Pool(keys[3]));
        Assert.Contains(rpc.Reads, read => read.Contains(keys[7]) && read.Contains(keys[8]));

        Dictionary<string, string> accounts = new(route.Accounts)
        {
            ["protocol_fee_recipient"] = keys[9], ["protocol_fee_recipient_token_account"] = keys[10]
        };

        route = route with { Accounts = accounts, BuybackRecipient = keys[^2] };
        IReadOnlyList<TransactionInstruction> instructions = routes.Instructions(route, new("fixture", keys[3], OrderSide.Buy, 10_000_000, 0, "Fixture"), keys[1], 1000);
        TransactionInstruction swap = Assert.Single(instructions, instruction => instruction.ProgramId.SequenceEqual(SolanaPrograms.Key(SolanaPrograms.Amm)));
        Assert.Equal(keys, swap.Keys.Select(key => key.PublicKey));
        Assert.Equal(new byte[] { 198, 46, 21, 82, 180, 217, 232, 112 }, swap.Data[..8]);
        Assert.Equal(10_000_000UL, BinaryPrimitives.ReadUInt64LittleEndian(swap.Data.AsSpan(8)));
        Assert.Equal(1000UL, BinaryPrimitives.ReadUInt64LittleEndian(swap.Data.AsSpan(16)));
        Assert.Equal(0, swap.Data[24]);
        Assert.Equal(new byte[] { 9 }, instructions[^1].Data);

        TransactionInstruction sale = Assert.Single(routes.Instructions(route, new("sell", keys[3], OrderSide.Sell, 1000, 0, "Exit"), keys[1], 10),
            instruction => instruction.ProgramId.SequenceEqual(SolanaPrograms.Key(SolanaPrograms.Amm)));

        Assert.Equal(24, sale.Keys.Count);
        Assert.Equal(new byte[] { 51, 230, 133, 164, 1, 127, 131, 173 }, sale.Data[..8]);
    }

    [Fact]
    public async Task TamperedMintAuthorityIsRefusedBeforeInstructionConstruction()
    {
        FixtureSolanaRpc rpc = new("pump-sell.json");
        string mint = Instruction(rpc, SolanaPrograms.Pump).GetProperty("accounts")[2].GetString()!;
        JsonElement original = rpc.Accounts[mint];
        byte[] bytes = Convert.FromBase64String(original.GetProperty("data")[0].GetString()!);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(46), 1);

        rpc.Accounts[mint] = JsonSerializer.SerializeToElement(new
            { owner = original.GetProperty("owner").GetString(), executable = false, lamports = 10000000, data = new[] { Convert.ToBase64String(bytes), "base64" } });

        await Assert.ThrowsAsync<InvalidDataException>(() => new PumpRoutes(rpc).ReadAsync(mint, new Account().PublicKey.Key, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PricingBoundsIncludeIntegerFeesSlippageAndRealLiquidity()
    {
        RouteSnapshot route = new("mint", SolanaPrograms.Token, "Pump", 1, 1_000_000, 1_000_000, 1_000_000, 1_000_000, [100], new Dictionary<string, string>(),
            SolanaPrograms.System);

        Assert.Equal(9801UL, route.BuyOutput(10000, 0));
        Assert.Equal(9702UL, route.BuyOutput(10000, 100));
        Assert.Equal(9801UL, route.SellOutput(10000, 0));
        Assert.Throws<InvalidDataException>(() => (route with { RealQuoteReserve = 1 }).SellOutput(10000, 0));
        Assert.True((route with { FeeRates = [200] }).BuyOutput(10000, 0) < route.BuyOutput(10000, 0));
    }

    [Theory, InlineData("pump", "FFE966C42F1AF41652EE753FE2F1E3F7CD4077D7E6F49FAF3138959C8B56064B"),
     InlineData("pump_fees", "D87B52305FD6B2EC487D4BA1E08A49990C23FA9B8B76092B2097DF0164FA3859"),
     InlineData("pump_amm", "2091433899B07D003D98118AE6CD3C628960FD393B40710B6E15BCE6D0E7F2D1")]
    public void ProtocolDefinitionsRemainPinned(string name, string expected)
    {
        using Stream resource = typeof(AnchorIdl).Assembly.GetManifestResourceStream($"SolastaBot.Exchange.Chain.Solana.Protocol.Fixtures.{name}.json")!;
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(resource)));
    }

    private static JsonElement Instruction(FixtureSolanaRpc rpc, string program) => rpc.Fixture.GetProperty("transaction").GetProperty("transaction").GetProperty("message")
        .GetProperty("instructions").EnumerateArray().Single(instruction => instruction.TryGetProperty("programId", out JsonElement address) && address.GetString() == program);
}