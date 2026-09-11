using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

/// <summary>
/// Summary statistics for one backtest run.
/// </summary>
/// <remarks>
/// <see cref="TotalFunding"/> is reported on its own rather than folded into fees. On perpetuals it
/// is frequently the largest single cost of a trend strategy, and a result that buries it inside a
/// net figure hides the one number most likely to decide whether the strategy is viable.
/// </remarks>
public sealed record BacktestMetrics(
    decimal StartingBalance,
    decimal FinalEquity,
    decimal TotalReturn,
    decimal MaxDrawdown,
    double Sharpe,
    decimal ProfitFactor,
    decimal WinRate,
    int TradeCount,
    int LiquidationCount,
    decimal GrossProfit,
    decimal GrossLoss,
    decimal TotalFees,
    decimal TotalFunding,
    decimal ExposureFraction)
{
    public static BacktestMetrics Compute(
        decimal startingBalance,
        IReadOnlyList<EquityPoint> equityCurve,
        IReadOnlyList<TradeRecord> trades,
        int barsInPosition)
    {
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(trades);

        decimal finalEquity = equityCurve.Count > 0 ? equityCurve[^1].Equity : startingBalance;
        decimal grossProfit = 0m;
        decimal grossLoss = 0m;
        decimal fees = 0m;
        decimal funding = 0m;
        int wins = 0;
        int liquidations = 0;

        foreach (TradeRecord trade in trades)
        {
            if (trade.NetPnl >= 0m)
            {
                grossProfit += trade.NetPnl;
                wins++;
            }
            else
            {
                grossLoss += -trade.NetPnl;
            }

            fees += trade.Fees;
            funding += trade.Funding;
            if (trade.Reason == ExitReason.Liquidation)
            {
                liquidations++;
            }
        }

        return new BacktestMetrics(
            StartingBalance: startingBalance,
            FinalEquity: finalEquity,
            TotalReturn: startingBalance <= 0m ? 0m : (finalEquity - startingBalance) / startingBalance,
            MaxDrawdown: ComputeMaxDrawdown(equityCurve),
            Sharpe: ComputeSharpe(equityCurve),
            ProfitFactor: grossLoss <= 0m ? (grossProfit > 0m ? decimal.MaxValue : 0m) : grossProfit / grossLoss,
            WinRate: trades.Count == 0 ? 0m : (decimal)wins / trades.Count,
            TradeCount: trades.Count,
            LiquidationCount: liquidations,
            GrossProfit: grossProfit,
            GrossLoss: grossLoss,
            TotalFees: fees,
            TotalFunding: funding,
            ExposureFraction: equityCurve.Count == 0 ? 0m : (decimal)barsInPosition / equityCurve.Count);
    }

    /// <summary>Deepest peak-to-trough fall in equity, as a fraction of the peak.</summary>
    private static decimal ComputeMaxDrawdown(IReadOnlyList<EquityPoint> curve)
    {
        decimal peak = 0m;
        decimal worst = 0m;

        foreach (EquityPoint point in curve)
        {
            if (point.Equity > peak)
            {
                peak = point.Equity;
            }

            if (peak > 0m)
            {
                decimal drawdown = (peak - point.Equity) / peak;
                if (drawdown > worst)
                {
                    worst = drawdown;
                }
            }
        }

        return worst;
    }

    /// <summary>
    /// Annualised Sharpe over per-bar returns, at a zero risk-free rate. The annualisation factor is
    /// derived from the curve's own span rather than assumed, so it stays correct across timeframes.
    /// </summary>
    private static double ComputeSharpe(IReadOnlyList<EquityPoint> curve)
    {
        if (curve.Count < 3)
        {
            return 0d;
        }

        double[] returns = new double[curve.Count - 1];
        for (int index = 1; index < curve.Count; index++)
        {
            decimal previous = curve[index - 1].Equity;
            returns[index - 1] = previous <= 0m
                ? 0d
                : (double)((curve[index].Equity - previous) / previous);
        }

        double mean = 0d;
        foreach (double value in returns)
        {
            mean += value;
        }

        mean /= returns.Length;

        double variance = 0d;
        foreach (double value in returns)
        {
            double deviation = value - mean;
            variance += deviation * deviation;
        }

        variance /= returns.Length - 1;
        double deviationPerBar = Math.Sqrt(variance);
        if (deviationPerBar <= 0d)
        {
            return 0d;
        }

        double years = (curve[^1].Time - curve[0].Time).TotalDays / 365.25d;
        double barsPerYear = years <= 0d ? returns.Length : returns.Length / years;
        return mean / deviationPerBar * Math.Sqrt(barsPerYear);
    }
}
