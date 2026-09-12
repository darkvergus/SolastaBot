namespace SolastaBot.Core.Backtest;

public sealed record WalkForwardFold(int Index, DateTime TrainFrom, DateTime TrainTo, DateTime TestFrom, DateTime TestTo, string ChosenCandidate, BacktestMetrics Train, BacktestMetrics Test);