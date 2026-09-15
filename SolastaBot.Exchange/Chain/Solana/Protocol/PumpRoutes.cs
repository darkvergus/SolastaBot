using System.Numerics;
using SolastaBot.Chain.Execution;
using SolastaBot.Exchange.Chain.Solana.Interfaces;
using Solnet.Rpc.Models;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public sealed class PumpRoutes(ISolanaRpc rpc)
{
    private readonly AnchorIdl pump = new("pump");
    private readonly AnchorIdl amm = new("pump_amm");
    private readonly AnchorIdl fees = new("pump_fees");

    public async Task<RouteSnapshot> ReadAsync(string mint, string user, CancellationToken cancellationToken)
    {
        string curveAddress = SolanaPrograms.Curve(mint);
        string poolAddress = SolanaPrograms.Pool(mint);
        AccountBatch initial = await SolanaAccounts.ReadAsync(rpc, [mint, curveAddress, poolAddress], cancellationToken);
        SolanaAccount curveAccount = initial.Accounts[1] ?? throw new InvalidDataException("Mint has no Pump bonding curve on this network.");
        Dictionary<string, object> curve = pump.Decode("BondingCurve", curveAccount);
        bool migrated = AnchorIdl.Flag(curve, "complete");
        string program = migrated ? SolanaPrograms.Amm : SolanaPrograms.Pump;
        string globalAddress = SolanaPrograms.Pda(program, SolanaPrograms.Seed(migrated ? "global_config" : "global"));
        string feeAddress = SolanaPrograms.Pda(SolanaPrograms.Fees, SolanaPrograms.Seed("fee_config"), SolanaPrograms.Key(program));
        Dictionary<string, object>? initialPool = migrated ? amm.Decode("Pool", initial.Accounts[2] ?? throw new InvalidDataException("Migration pool is not yet available; keep the position pending.")) : null;
        string[] addresses = migrated
            ? [mint, curveAddress, poolAddress, globalAddress, feeAddress, AnchorIdl.Address(initialPool!, "pool_base_token_account"), AnchorIdl.Address(initialPool!, "pool_quote_token_account"), program]
            : [mint, curveAddress, globalAddress, feeAddress, program];
        AccountBatch batch = await SolanaAccounts.ReadAsync(rpc, addresses, cancellationToken, initial.Slot);
        SolanaAccount mintAccount = batch.Accounts[0] ?? throw new InvalidDataException("Missing mint.");
        ulong supply = TokenAccounts.MintSupply(mintAccount);
        if (batch.Accounts[^1]?.Executable != true)
        {
            throw new InvalidDataException("Trading program is not deployed on this network.");
        }

        curve = pump.Decode("BondingCurve", batch.Accounts[1]!);
        if (AnchorIdl.Flag(curve, "complete") != migrated)
        {
            throw new InvalidDataException("Migration changed during quote; refresh the route.");
        }

        RejectSpecial(curve);
        if (curve.TryGetValue("quote_mint", out object? curveQuote) && (string)curveQuote != SolanaPrograms.System)
        {
            throw new InvalidDataException("Only native SOL bonding curves are supported.");
        }

        Dictionary<string, object> market = migrated ? amm.Decode("Pool", batch.Accounts[2]!) : curve;
        RejectSpecial(market);
        Dictionary<string, object> global = (migrated ? amm : pump).Decode(migrated ? "GlobalConfig" : "Global", batch.Accounts[migrated ? 3 : 2]!);
        Dictionary<string, object> feeConfig = fees.Decode("FeeConfig", batch.Accounts[migrated ? 4 : 3] ?? throw new InvalidDataException("Dynamic fee config is unavailable."));
        BigInteger baseReserve;
        BigInteger quoteReserve;
        BigInteger realBase;
        BigInteger realQuote;
        string creator = AnchorIdl.Address(market, migrated ? "coin_creator" : "creator");
        if (migrated && creator == SolanaPrograms.System)
        {
            throw new InvalidDataException("Canonical migration pool has no coin creator.");
        }
        if (migrated)
        {
            if (AnchorIdl.Address(market, "base_mint") != mint || AnchorIdl.Address(market, "quote_mint") != SolanaPrograms.WrappedSol ||
                AnchorIdl.Address(market, "creator") != SolanaPrograms.PoolAuthority(mint) || AnchorIdl.Number(market, "index") != 0)
            {
                throw new InvalidDataException("Pool is not the canonical SOL migration pool.");
            }

            if (AnchorIdl.Number(market, "virtual_quote_reserves") != 0 || (AnchorIdl.Number(global, "disable_flags") & 24) != 0)
            {
                throw new InvalidDataException("Boosted or trading-disabled pools are unsupported.");
            }

            if (AnchorIdl.Address(market, "pool_base_token_account") != addresses[5] || AnchorIdl.Address(market, "pool_quote_token_account") != addresses[6])
            {
                throw new InvalidDataException("Pool vaults changed during quote.");
            }

            baseReserve = TokenAccounts.Balance(batch.Accounts[5]!, mint, poolAddress, mintAccount.Owner);
            quoteReserve = TokenAccounts.Balance(batch.Accounts[6]!, SolanaPrograms.WrappedSol, poolAddress, SolanaPrograms.Token);
            realBase = baseReserve;
            realQuote = quoteReserve;
        }
        else
        {
            baseReserve = AnchorIdl.Number(curve, "virtual_token_reserves");
            quoteReserve = AnchorIdl.Number(curve, "virtual_quote_reserves");
            realBase = AnchorIdl.Number(curve, "real_token_reserves");
            realQuote = AnchorIdl.Number(curve, "real_quote_reserves");
        }
        if (baseReserve <= 0 || quoteReserve <= 0)
        {
            throw new InvalidDataException("Empty route reserves.");
        }

        IReadOnlyList<BigInteger> rates = FeeRates(feeConfig, quoteReserve * supply / baseReserve, migrated, creator, market, global);
        string feeRecipient = migrated ? (string)((List<object>)global["protocol_fee_recipients"])[0] : AnchorIdl.Address(global, "fee_recipient");
        string buyback = (string)((List<object>)global["buyback_fee_recipients"])[0];
        if (feeRecipient == SolanaPrograms.System || buyback == SolanaPrograms.System)
        {
            throw new InvalidDataException("Fee recipient is uninitialized.");
        }

        Dictionary<string, string> accounts = new()
        {
            ["user"] = user, ["mint"] = mint, ["base_mint"] = mint, ["quote_mint"] = SolanaPrograms.WrappedSol,
            ["global"] = globalAddress, ["global_config"] = globalAddress, ["fee_recipient"] = feeRecipient, ["protocol_fee_recipient"] = feeRecipient,
            ["fee_config"] = feeAddress, ["fee_program"] = SolanaPrograms.Fees, ["program"] = program,
            ["system_program"] = SolanaPrograms.System, ["token_program"] = mintAccount.Owner, ["base_token_program"] = mintAccount.Owner,
            ["quote_token_program"] = SolanaPrograms.Token, ["associated_token_program"] = SolanaPrograms.AssociatedToken,
            ["event_authority"] = SolanaPrograms.Pda(program, SolanaPrograms.Seed("__event_authority")),
            ["global_volume_accumulator"] = SolanaPrograms.Pda(program, SolanaPrograms.Seed("global_volume_accumulator")),
            ["user_volume_accumulator"] = SolanaPrograms.Pda(program, SolanaPrograms.Seed("user_volume_accumulator"), SolanaPrograms.Key(user)),
            ["associated_user"] = SolanaPrograms.Ata(user, mint, mintAccount.Owner), ["user_base_token_account"] = SolanaPrograms.Ata(user, mint, mintAccount.Owner),
            ["user_quote_token_account"] = SolanaPrograms.Ata(user, SolanaPrograms.WrappedSol, SolanaPrograms.Token)
        };
        if (migrated)
        {
            string vault = SolanaPrograms.Pda(program, SolanaPrograms.Seed("creator_vault"), SolanaPrograms.Key(creator));
            accounts["pool"] = poolAddress;
            accounts["pool_base_token_account"] = addresses[5];
            accounts["pool_quote_token_account"] = addresses[6];
            accounts["protocol_fee_recipient_token_account"] = SolanaPrograms.Ata(feeRecipient, SolanaPrograms.WrappedSol, SolanaPrograms.Token);
            accounts["coin_creator_vault_authority"] = vault;
            accounts["coin_creator_vault_ata"] = SolanaPrograms.Ata(vault, SolanaPrograms.WrappedSol, SolanaPrograms.Token);
        }
        else
        {
            accounts["bonding_curve"] = curveAddress;
            accounts["associated_bonding_curve"] = SolanaPrograms.Ata(curveAddress, mint, mintAccount.Owner);
            accounts["creator_vault"] = SolanaPrograms.Pda(program, SolanaPrograms.Seed("creator-vault"), SolanaPrograms.Key(creator));
        }
        return new(mint, mintAccount.Owner, migrated ? "PumpSwap" : "Pump", batch.Slot, baseReserve, quoteReserve, realBase, realQuote, rates, accounts, buyback);
    }

    public IReadOnlyList<TransactionInstruction> Instructions(RouteSnapshot route, OrderIntent intent, string user, ulong minimumOutput)
    {
        bool buy = intent.Side == OrderSide.Buy;
        bool migrated = route.Route == "PumpSwap";
        List<TransactionInstruction> instructions = [];
        if (buy)
        {
            instructions.Add(TokenInstructions.CreateAta(user, intent.Mint, route.TokenProgram));
        }

        string wrapped = SolanaPrograms.Ata(user, SolanaPrograms.WrappedSol, SolanaPrograms.Token);
        if (migrated)
        {
            instructions.Add(TokenInstructions.CreateAta(user, SolanaPrograms.WrappedSol, SolanaPrograms.Token));
            if (buy)
            {
                instructions.Add(TokenInstructions.Transfer(user, wrapped, intent.Amount));
                instructions.Add(TokenInstructions.Sync(wrapped));
            }
        }
        List<AccountMeta> remaining =
        [
            TokenInstructions.Read(SolanaPrograms.Pda(migrated ? SolanaPrograms.Amm : SolanaPrograms.Pump,
                SolanaPrograms.Seed(migrated ? "pool-v2" : "bonding-curve-v2"), SolanaPrograms.Key(intent.Mint))),

            migrated ? TokenInstructions.Read(route.BuybackRecipient) : TokenInstructions.Write(route.BuybackRecipient)
        ];

        if (migrated)
        {
            remaining.Add(TokenInstructions.Write(SolanaPrograms.Ata(route.BuybackRecipient, SolanaPrograms.WrappedSol, SolanaPrograms.Token)));
        }

        string instruction = buy ? migrated ? "buy_exact_quote_in" : "buy_exact_sol_in" : "sell";
        instructions.Add((migrated ? amm : pump).Instruction(instruction, route.Accounts, TokenInstructions.Amounts(intent.Amount, minimumOutput, buy), remaining));
        if (migrated)
        {
            instructions.Add(TokenInstructions.Close(wrapped, user));
        }

        return instructions;
    }

    private static void RejectSpecial(Dictionary<string, object> fields)
    {
        if (AnchorIdl.Flag(fields, "is_mayhem_mode") || AnchorIdl.Flag(fields, "is_cashback_coin") || AnchorIdl.Flag(fields, "is_holder_reward"))
        {
            throw new InvalidDataException("Mayhem, cashback and holder-reward routes are not supported.");
        }
    }

    private static IReadOnlyList<BigInteger> FeeRates(Dictionary<string, object> config, BigInteger marketCap, bool migrated, string creator,
        Dictionary<string, object> market, Dictionary<string, object> global)
    {
        List<object> tiers = (List<object>)config["fee_tiers"];
        if (tiers.Count == 0)
        {
            throw new InvalidDataException("Empty fee tiers.");
        }

        Dictionary<string, object> selected = (Dictionary<string, object>)tiers[0];
        BigInteger previous = -1;
        foreach (Dictionary<string, object> tier in tiers.Cast<Dictionary<string, object>>())
        {
            BigInteger threshold = AnchorIdl.Number(tier, "market_cap_lamports_threshold");
            if (threshold <= previous)
            {
                throw new InvalidDataException("Fee tiers are not strictly ordered.");
            }

            previous = threshold;
            if (threshold <= marketCap)
            {
                selected = tier;
            }
        }
        Dictionary<string, object> schedule = (Dictionary<string, object>)selected["fees"];
        BigInteger creatorFee = creator == SolanaPrograms.System ? 0 : AnchorIdl.Flag(global, "creator_fee_configurable") && AnchorIdl.Number(market, "creator_fee_bps") > 0
            ? AnchorIdl.Number(market, "creator_fee_bps") : AnchorIdl.Number(schedule, "creator_fee_bps");
        BigInteger[] rates = [migrated ? AnchorIdl.Number(schedule, "lp_fee_bps") : 0, AnchorIdl.Number(schedule, "protocol_fee_bps"), creatorFee];
        if (rates.Any(rate => rate < 0 || rate > 10000) || rates.Aggregate(BigInteger.Zero, (total, rate) => total + rate) >= 10000)
        {
            throw new InvalidDataException("Invalid protocol fees.");
        }

        return rates;
    }
}
