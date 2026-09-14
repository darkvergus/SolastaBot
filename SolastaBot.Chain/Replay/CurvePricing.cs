using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Replay;

public static class CurvePricing
{
    public const decimal LamportsPerSol = 1_000_000_000m;

    public static decimal? Buy(CurveObservation observation, ReplayOptions options)
    {
        if (!observation.HasReserves || observation.Complete != false || observation.Error is not null)
        {
            return null;
        }

        decimal budget = options.TradeSizeSol * LamportsPerSol;
        decimal quoteInput = decimal.Floor(budget / (1m + options.FeeBasisPoints / 10_000m));
        decimal tokens = decimal.Floor(observation.VirtualTokenReserves!.Value * (quoteInput / (observation.VirtualQuoteReserves!.Value + quoteInput)));
        if (tokens <= 0m || tokens >= observation.RealTokenReserves!.Value)
        {
            return null;
        }

        decimal received = decimal.Floor(tokens * (1m - options.SlippageBasisPoints / 10_000m));
        return received > 0m ? received : null;
    }

    public static decimal? Sell(CurveObservation observation, decimal tokens, ReplayOptions options)
    {
        if (!observation.HasReserves || observation.Complete != false || observation.Error is not null || tokens <= 0m)
        {
            return null;
        }

        decimal quoteOutput = decimal.Floor(observation.VirtualQuoteReserves!.Value * (tokens / (observation.VirtualTokenReserves!.Value + tokens)));
        if (quoteOutput > observation.RealQuoteReserves!.Value)
        {
            return null;
        }

        decimal fee = decimal.Ceiling(quoteOutput * options.FeeBasisPoints / 10_000m);
        decimal proceeds = decimal.Floor((quoteOutput - fee) * (1m - options.SlippageBasisPoints / 10_000m));
        return proceeds / LamportsPerSol - options.TransactionCostSol;
    }
}
