using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Strategy;

/// <summary>
/// Everything a strategy is permitted to see: the bar that just closed, the instrument, and the
/// current position.
/// </summary>
/// <remarks>
/// There is deliberately no clock, no order book, no account balance and no forming candle here.
/// A strategy that cannot reach the wall clock cannot behave differently in a backtest than it does
/// live, and a strategy that cannot see the forming candle cannot accidentally use a price that had
/// not happened yet. Balances belong to the risk gate, not the strategy, so that sizing decisions
/// stay in one place.
/// </remarks>
public readonly record struct MarketSnapshot(Instrument Instrument, Candle Candle, PositionState Position);
