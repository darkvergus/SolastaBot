using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Trading.Interfaces;

public interface IOrderQuoteRouter
{
    decimal? Buy(CurveObservation observation, PaperTradingOptions options);
    decimal? Sell(CurveObservation observation, decimal tokens, PaperTradingOptions options);
}
