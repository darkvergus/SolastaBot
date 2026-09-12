using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

public sealed record WalkForwardReport(IReadOnlyList<WalkForwardFold> Folds, decimal StartingBalance, decimal FinalEquity, IReadOnlyList<TradeRecord> Trades, IReadOnlyList<EquityPoint> EquityCurve,
    BacktestMetrics Combined)
{
    public int ProfitableFolds => Folds.Count(fold => fold.Test.TotalReturn > 0m);

    /// <summary>
    /// How often the training window chose a different parameter set than the fold before it.
    /// </summary>
    /// <remarks>
    /// A set that keeps changing is the clearest sign that training is fitting noise. A robust edge
    /// should keep picking roughly the same parameters as the window rolls.
    /// </remarks>
    public int ParameterChanges
    {
        get
        {
            int changes = 0;
            for (int index = 1; index < Folds.Count; index++)
            {
                if (Folds[index].ChosenCandidate != Folds[index - 1].ChosenCandidate)
                {
                    changes++;
                }
            }

            return changes;
        }
    }
}