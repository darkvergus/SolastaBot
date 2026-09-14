using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Trading;

public sealed record TradingEvent(long Sequence, decimal At, string Kind, string Mint, string Reason, decimal? AmountSol = null, decimal? Tokens = null, CurveObservation? Observation = null);
