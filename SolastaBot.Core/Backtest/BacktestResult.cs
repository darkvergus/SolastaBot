using System;
using System.Collections.Generic;
using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

public sealed record BacktestResult(string StrategyName, string Symbol, string SlippageProfile, DateTime From, DateTime To, IReadOnlyList<TradeRecord> Trades, IReadOnlyList<EquityPoint> EquityCurve,
    BacktestMetrics Metrics);
