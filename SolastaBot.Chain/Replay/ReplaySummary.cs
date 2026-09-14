namespace SolastaBot.Chain.Replay;

public sealed record ReplaySummary(string Arm, int Admitted, int Entered, int Resolved, int UnknownExits, int MissingEntries, int UnavailableEntries, int Unsupported,
    decimal? ResolvedMedianReturn, decimal? ResolvedMeanReturn, decimal? ResolvedWinRate, decimal? StressMeanReturn, decimal StressPnlSol)
{
    public static ReplaySummary Compute(string arm, IReadOnlyList<ReplayTrade> trades)
    {
        ReplayTrade[] entered = [.. trades.Where(trade => trade.EnteredAt.HasValue)];
        decimal[] returns = [.. entered.Where(trade => trade.NetReturn.HasValue).Select(trade => trade.NetReturn!.Value).Order()];
        decimal? median = returns.Length == 0 ? null : returns.Length % 2 == 0 ? (returns[returns.Length / 2 - 1] + returns[returns.Length / 2]) / 2m : returns[returns.Length / 2];

        return new(arm, trades.Count, entered.Length, returns.Length, entered.Length - returns.Length, trades.Count(trade => trade.Outcome == ReplayOutcome.MissingEntry),
            trades.Count(trade => trade.Outcome == ReplayOutcome.EntryUnavailable),
            trades.Count(trade => trade.Outcome == ReplayOutcome.UnsupportedMarket), median, returns.Length == 0 ? null : returns.Average(),
            returns.Length == 0 ? null : (decimal)returns.Count(value => value > 0m) / returns.Length,
            entered.Length == 0 ? null : entered.Average(trade => trade.StressPnlSol!.Value / trade.EntryCostSol), entered.Sum(trade => trade.StressPnlSol ?? 0m));
    }
}
