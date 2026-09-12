using SolastaBot.Core.Strategy;

namespace SolastaBot.Core.Backtest;

/// <summary>One parameter set under consideration, named for the report.</summary>
public sealed record StrategyCandidate(string Name, Func<IStrategy> Create);