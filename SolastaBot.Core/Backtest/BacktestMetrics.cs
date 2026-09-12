using System;
using System.Collections.Generic;
using System.Linq;
using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

/// <summary>
/// Summary statistics for one backtest run.
/// </summary>
/// <remarks>
/// <see cref="TotalFunding"/> is reported on its own rather than folded into fees. On perpetuals it
/// is frequently the largest single cost of a trend strategy, and a result that buries it inside a
/// net figure hides the one number most likely to decide whether the strategy is viable.
/// <para>
/// <see cref="GrossPnl"/> separates the two diagnoses a losing run can have. Positive here means the
/// trades made money and fees and funding took it away, which trading less often can fix. Negative
/// means the trades lost money on their own, and no amount of execution work will rescue that.
/// </para>
/// <para>
/// It is gross of fees and funding but <em>net of slippage</em>, because slippage is applied to the
/// fill price and is therefore already inside every <see cref="TradeRecord.GrossPnl"/>. To see the
/// edge with no execution assumption at all, run the same backtest at the <c>none</c> slippage
/// profile. <see cref="NetProfit"/> and <see cref="NetLoss"/> are net of everything, and are named
/// for it.
/// </para>
/// </remarks>
public sealed record BacktestMetrics(decimal StartingBalance, decimal FinalEquity, decimal TotalReturn, decimal MaxDrawdown, double Sharpe, decimal ProfitFactor, decimal WinRate,
    int TradeCount, int LiquidationCount, decimal GrossPnl, decimal NetProfit, decimal NetLoss, decimal TotalFees, decimal TotalFunding, decimal ExposureFraction)
{
    public static BacktestMetrics Compute(decimal startingBalance, IReadOnlyList<EquityPoint> equityCurve, IReadOnlyList<TradeRecord> trades, int barsInPosition)
    {
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(trades);

        decimal finalEquity = equityCurve.Count > 0 ? equityCurve[^1].Equity : startingBalance;
        decimal grossPnl = 0m;
        decimal netProfit = 0m;
        decimal netLoss = 0m;
        decimal fees = 0m;
        decimal funding = 0m;
        int wins = 0;
        int liquidations = 0;

        foreach (TradeRecord trade in trades)
        {
            if (trade.NetPnl >= 0m)
            {
                netProfit += trade.NetPnl;
                wins++;
            }
            else
            {
                netLoss += -trade.NetPnl;
            }

            grossPnl += trade.GrossPnl;
            fees += trade.Fees;
            funding += trade.Funding;
            if (trade.Reason == ExitReason.Liquidation)
            {
                liquidations++;
            }
        }

        return new(StartingBalance: startingBalance, FinalEquity: finalEquity, TotalReturn: startingBalance <= 0m ? 0m : (finalEquity - startingBalance) / startingBalance,
            MaxDrawdown: ComputeMaxDrawdown(equityCurve), Sharpe: ComputeSharpe(equityCurve), ProfitFactor: netLoss <= 0m ? netProfit > 0m ? decimal.MaxValue : 0m : netProfit / netLoss,
            WinRate: trades.Count == 0 ? 0m : (decimal)wins / trades.Count, TradeCount: trades.Count, LiquidationCount: liquidations, GrossPnl: grossPnl, NetProfit: netProfit, NetLoss: netLoss,
            TotalFees: fees, TotalFunding: funding, ExposureFraction: equityCurve.Count == 0 ? 0m : (decimal)barsInPosition / equityCurve.Count);
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
            returns[index - 1] = previous <= 0m ? 0d : (double)((curve[index].Equity - previous) / previous);
        }

        double mean = returns.Sum();

        mean /= returns.Length;

        double variance = returns.Select(value => value - mean).Select(deviation => deviation * deviation).Sum();

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
