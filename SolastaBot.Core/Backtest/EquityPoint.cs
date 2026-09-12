namespace SolastaBot.Core.Backtest;

/// <summary>One mark-to-market sample, taken at every bar close.</summary>
public readonly record struct EquityPoint(DateTime Time, decimal Equity, decimal WalletBalance);
