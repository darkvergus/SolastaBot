using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;
using SolastaBot.Chain.Trading.Interfaces;

namespace SolastaBot.Chain.Trading;

public sealed class PaperOrderRouter : IOrderQuoteRouter
{
    public decimal? Buy(CurveObservation observation, PaperTradingOptions options) => CurvePricing.Buy(observation, options.Execution);

    public decimal? Sell(CurveObservation observation, decimal tokens, PaperTradingOptions options) => CurvePricing.Sell(observation, tokens, options.Execution);
}
