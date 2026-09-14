using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;

namespace SolastaBot.Chain.Trading;

public sealed class PaperOrderRouter
{
    public decimal? Buy(CurveObservation observation, PaperTradingOptions options) => CurvePricing.Buy(observation, options.Execution);

    public decimal? Sell(CurveObservation observation, decimal tokens, PaperTradingOptions options) => CurvePricing.Sell(observation, tokens, options.Execution);
}
