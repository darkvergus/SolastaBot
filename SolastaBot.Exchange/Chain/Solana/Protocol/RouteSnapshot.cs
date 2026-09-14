using System.Numerics;

namespace SolastaBot.Exchange.Chain.Solana.Protocol;

public sealed record RouteSnapshot(string Mint, string TokenProgram, string Route, ulong Slot, BigInteger BaseReserve, BigInteger QuoteReserve,
    BigInteger RealBaseReserve, BigInteger RealQuoteReserve, IReadOnlyList<BigInteger> FeeRates, IReadOnlyDictionary<string, string> Accounts, string BuybackRecipient)
{
    public ulong BuyOutput(ulong lamports, int slippageBps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slippageBps);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slippageBps, 10000);
        BigInteger spendable = (new BigInteger(lamports) - FeeRates.Count - 1) * 10000 / (10000 + FeeRates.Aggregate(BigInteger.Zero, (total, rate) => total + rate));
        if (spendable <= 0 || BaseReserve <= 0 || QuoteReserve <= 0)
        {
            throw new InvalidDataException("No executable liquidity.");
        }

        BigInteger tokens = BaseReserve * spendable / (QuoteReserve + spendable);
        tokens = BigInteger.Min(tokens, RealBaseReserve) * (10000 - slippageBps) / 10000;
        if (tokens <= 0)
        {
            throw new InvalidDataException("Buy output rounds to zero.");
        }

        return checked((ulong)tokens);
    }

    public ulong SellOutput(ulong tokens, int slippageBps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slippageBps);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slippageBps, 10000);
        if (BaseReserve <= 0 || QuoteReserve <= 0)
        {
            throw new InvalidDataException("No executable liquidity.");
        }

        BigInteger quote = QuoteReserve * tokens / (BaseReserve + tokens);
        if (quote > RealQuoteReserve)
        {
            throw new InvalidDataException("Real reserves cannot cover the sale.");
        }

        BigInteger fees = FeeRates.Aggregate(BigInteger.Zero, (total, rate) => total + (quote * rate + 9999) / 10000);
        BigInteger output = (quote - fees) * (10000 - slippageBps) / 10000;
        if (output <= 0)
        {
            throw new InvalidDataException("Sell output rounds to zero.");
        }

        return checked((ulong)output);
    }
}
