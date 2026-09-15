using SolastaBot.Host.Portfolio;

namespace SolastaBot.Host.Research;

public sealed record WindowEvaluation(StrategySettings Settings, StrategyAccount Account, int Count, decimal First, decimal Last, int CoveredDays, int GapCount, string Hash);
