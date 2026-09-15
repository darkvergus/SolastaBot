using System.Globalization;
using System.Text.Json;
using SolastaBot.Chain.Execution;
using SolastaBot.Exchange.Chain.Solana.Interfaces;
using SolastaBot.Exchange.Chain.Solana.Protocol;
using Solnet.Rpc.Models;
using Solnet.Rpc.Builders;
using Solnet.Wallet.Utilities;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed class SolanaOrderRouter(ISolanaRpc rpc, ITransactionSigner signer, ConnectedOptions options) : IConnectedOrderRouter
{
    public async Task<ExecutionOrder> PrepareAsync(OrderIntent intent, CancellationToken cancellationToken)
    {
        options.Validate();
        if (intent.Amount == 0)
        {
            throw new ArgumentException("Order amount must be positive.");
        }

        PumpRoutes routes = new(rpc);
        RouteSnapshot route = await routes.ReadAsync(intent.Mint, signer.Address, cancellationToken);
        if (route.Route == "PumpSwap")
        {
            AccountBatch wrapped = await SolanaAccounts.ReadAsync(rpc, [SolanaPrograms.Ata(signer.Address, SolanaPrograms.WrappedSol, SolanaPrograms.Token)], cancellationToken, route.Slot);
            if (wrapped.Accounts[0] is not null)
            {
                throw new InvalidDataException("Dedicated trading wallet must have no pre-existing WSOL account before a swap.");
            }
        }
        ulong minimum = intent.Side == OrderSide.Buy ? route.BuyOutput(intent.Amount, options.SlippageBasisPoints) : route.SellOutput(intent.Amount, options.SlippageBasisPoints);
        IReadOnlyList<TransactionInstruction> instructions = routes.Instructions(route, intent, signer.Address, minimum);
        JsonElement block = await rpc.CallAsync("getLatestBlockhash", [new { commitment = "confirmed", minContextSlot = route.Slot }], cancellationToken);
        JsonElement value = block.GetProperty("value");
        string blockhash = value.GetProperty("blockhash").GetString()!;
        byte[] bytes;
        if (options.Mode == "Simulate")
        {
            TransactionBuilder builder = new TransactionBuilder().SetFeePayer(new(signer.Address)).SetRecentBlockHash(blockhash);
            foreach (TransactionInstruction instruction in instructions)
            {
                builder.AddInstruction(instruction);
            }
            bytes = [1, .. new byte[64], .. builder.CompileMessage()];
        }
        else
        {
            bytes = signer.Sign(blockhash, instructions);
        }
        if (bytes.Length > 1232 || bytes[0] != 1)
        {
            throw new InvalidDataException("Transaction exceeds the packet size or has unexpected signers.");
        }

        JsonElement fee = await rpc.CallAsync("getFeeForMessage", [Convert.ToBase64String(bytes.AsSpan(65)), new { commitment = "confirmed" }], cancellationToken);
        if (fee.GetProperty("value").ValueKind == JsonValueKind.Null || fee.GetProperty("value").GetUInt64() > options.FeeReserveLamports)
        {
            throw new InvalidDataException("Network fee exceeds the reserved budget.");
        }

        ulong reserve = checked(options.FeeReserveLamports + (intent.Side == OrderSide.Buy ? intent.Amount : 0));
        return new(intent, OrderStatus.Signed, Convert.ToBase64String(bytes), Encoders.Base58.EncodeData(bytes, 1, 64), value.GetProperty("lastValidBlockHeight").GetUInt64(),
            reserve, minimum, route.Route, route.Slot, Quote: new((decimal)route.BaseReserve, (decimal)route.QuoteReserve, (decimal)route.RealBaseReserve,
                (decimal)route.RealQuoteReserve, [.. route.FeeRates.Select(rate => (decimal)rate)], route.Accounts, "81091419e4457566469d4e2a27f64ed84d42419c"));
    }

    public async Task<string> SimulateAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        AccountBatch before = await SolanaAccounts.ReadAsync(rpc, [signer.Address], cancellationToken, order.QuoteSlot);
        ulong balance = before.Accounts[0]?.Lamports ?? 0;
        JsonElement result = await rpc.CallAsync("simulateTransaction", [order.Transaction, new
        {
            encoding = "base64", sigVerify = options.Mode == "Devnet", replaceRecentBlockhash = false, commitment = "confirmed", minContextSlot = order.QuoteSlot,
            accounts = new { encoding = "base64", addresses = new[] { signer.Address } }
        }], cancellationToken);
        JsonElement value = result.GetProperty("value");
        if (value.GetProperty("err").ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException($"Transaction simulation rejected: {value.GetProperty("err").GetRawText()}; {value.GetProperty("logs").GetRawText()}");
        }

        JsonElement simulatedWallet = value.GetProperty("accounts")[0];
        if (simulatedWallet.ValueKind == JsonValueKind.Null || balance - (decimal)simulatedWallet.GetProperty("lamports").GetUInt64() > order.ReservedLamports)
        {
            throw new InvalidOperationException("Simulated wallet debit exceeds the order and fee reserve.");
        }

        return $"Simulation succeeded; units={value.GetProperty("unitsConsumed").GetUInt64()}; minimum output={order.MinimumOutput}; no transaction sent.";
    }

    public async Task SubmitAsync(ExecutionOrder order, Func<bool> authorized, CancellationToken cancellationToken)
    {
        if (options.Mode != "Devnet")
        {
            throw new InvalidOperationException("Sending is disabled in this mode.");
        }

        JsonElement response = await rpc.SendAsync(order.Transaction, order.QuoteSlot, authorized, cancellationToken);
        if (response.GetString() != order.Signature)
        {
            throw new InvalidDataException("RPC returned a different signature. Submission is unresolved.");
        }
    }

    public async Task<ExecutionReceipt?> ReceiptAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        JsonElement result = await rpc.CallAsync("getTransaction", [order.Signature, new { encoding = "json", commitment = "finalized", maxSupportedTransactionVersion = 0 }], cancellationToken);
        if (result.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        JsonElement transaction = result.GetProperty("transaction");
        if (transaction.GetProperty("signatures")[0].GetString() != order.Signature || transaction.GetProperty("message").GetProperty("accountKeys")[0].GetString() != signer.Address)
        {
            throw new InvalidDataException("Receipt signature or fee payer mismatch.");
        }

        JsonElement meta = result.GetProperty("meta");
        if (meta.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        long change = checked((long)meta.GetProperty("postBalances")[0].GetUInt64() - (long)meta.GetProperty("preBalances")[0].GetUInt64());
        long tokenChange = checked(TokenTotal(meta.GetProperty("postTokenBalances"), order.Intent.Mint) - TokenTotal(meta.GetProperty("preTokenBalances"), order.Intent.Mint));
        return new(meta.GetProperty("err").ValueKind != JsonValueKind.Null, change, tokenChange, meta.GetProperty("fee").GetUInt64(), result.GetProperty("slot").GetUInt64());
    }

    public async Task<bool> ExpiredAsync(ExecutionOrder order, CancellationToken cancellationToken)
    {
        ulong height = (await rpc.CallAsync("getBlockHeight", [new { commitment = "finalized" }], cancellationToken)).GetUInt64();
        if (height <= order.LastValidBlockHeight)
        {
            return false;
        }

        JsonElement statuses = await rpc.CallAsync("getSignatureStatuses", [new[] { order.Signature }, new { searchTransactionHistory = true }], cancellationToken);
        return statuses.GetProperty("value")[0].ValueKind == JsonValueKind.Null;
    }

    public async Task<WalletSnapshot> WalletAsync(CancellationToken cancellationToken, ulong minimumSlot = 0)
    {
        JsonElement balance = await rpc.CallAsync("getBalance", [signer.Address, new { commitment = "finalized", minContextSlot = minimumSlot }], cancellationToken);
        Dictionary<string, ulong> tokens = [];
        ulong slot = balance.GetProperty("context").GetProperty("slot").GetUInt64();
        foreach (string program in new[] { SolanaPrograms.Token, SolanaPrograms.Token2022 })
        {
            JsonElement accounts = await rpc.CallAsync("getTokenAccountsByOwner", [signer.Address, new { programId = program }, new { encoding = "jsonParsed", commitment = "finalized", minContextSlot = slot }], cancellationToken);
            foreach (JsonElement account in accounts.GetProperty("value").EnumerateArray())
            {
                JsonElement info = account.GetProperty("account").GetProperty("data").GetProperty("parsed").GetProperty("info");
                ulong amount = ulong.Parse(info.GetProperty("tokenAmount").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
                if (amount == 0)
                {
                    continue;
                }

                string mint = info.GetProperty("mint").GetString()!;
                if (info.GetProperty("owner").GetString() != signer.Address || info.GetProperty("state").GetString() != "initialized")
                {
                    throw new InvalidDataException("Unusable wallet token account.");
                }

                if (account.GetProperty("pubkey").GetString() != SolanaPrograms.Ata(signer.Address, mint, program))
                {
                    throw new InvalidDataException("Non-associated holdings require manual reconciliation.");
                }

                tokens[mint] = checked(tokens.GetValueOrDefault(mint) + amount);
            }
        }
        return new(balance.GetProperty("value").GetUInt64(), tokens, slot);
    }

    private long TokenTotal(JsonElement balances, string mint)
    {
        long total = 0;
        foreach (JsonElement balance in balances.EnumerateArray())
        {
            if (balance.GetProperty("mint").GetString() == mint && balance.TryGetProperty("owner", out JsonElement owner) && owner.GetString() == signer.Address)
            {
                total = checked(total + long.Parse(balance.GetProperty("uiTokenAmount").GetProperty("amount").GetString()!, CultureInfo.InvariantCulture));
            }
        }
        return total;
    }
}
