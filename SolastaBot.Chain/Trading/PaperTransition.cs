namespace SolastaBot.Chain.Trading;

public sealed record PaperTransition(PaperTradingState State, IReadOnlyList<TradingEvent> Events);
