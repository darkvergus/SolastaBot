using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;

namespace SolastaBot.Core.Backtest;

public sealed record BacktestRequest
{
    public required Instrument Instrument { get; init; }

    /// <summary>Closed bars in ascending time order, with no gaps and no duplicates.</summary>
    public required IReadOnlyList<Candle> Candles { get; init; }

    /// <summary>Funding settlements in ascending time order. Empty means funding is not modelled.</summary>
    public IReadOnlyList<FundingEvent> Funding { get; init; } = [];

    public required IStrategy Strategy { get; init; }

    public required RiskGate Risk { get; init; }

    public BacktestOptions Options { get; init; } = new();
}
