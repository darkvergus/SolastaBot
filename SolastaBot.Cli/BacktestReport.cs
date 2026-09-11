using System.Globalization;
using System.Text;
using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Data.Integrity;

namespace SolastaBot.Cli;

/// <summary>
/// Formats a backtest result for reading.
/// </summary>
/// <remarks>
/// Fees and funding are printed as their own lines, not folded into the return. On perpetuals they
/// routinely add up to more than the strategy's edge, and a report that shows only the bottom line
/// makes that impossible to notice.
/// </remarks>
internal static class BacktestReport
{
    internal static string Render(BacktestResult result, IntegrityReport integrity, int fundingEvents)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(integrity);

        BacktestMetrics metrics = result.Metrics;
        StringBuilder text = new();

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{result.Symbol}  {result.StrategyName}  slippage={result.SlippageProfile}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{result.From:yyyy-MM-dd} to {result.To:yyyy-MM-dd}   {integrity.Count} bars, {fundingEvents} funding settlements");
        text.AppendLine();

        Row(text, "Starting balance", Money(metrics.StartingBalance));
        Row(text, "Final equity", Money(metrics.FinalEquity));
        Row(text, "Total return", Percent(metrics.TotalReturn));
        Row(text, "Max drawdown", Percent(metrics.MaxDrawdown));
        Row(text, "Sharpe", metrics.Sharpe.ToString("F2", CultureInfo.InvariantCulture));
        Row(text, "Profit factor", metrics.ProfitFactor == decimal.MaxValue
            ? "no losses"
            : metrics.ProfitFactor.ToString("F2", CultureInfo.InvariantCulture));
        text.AppendLine();

        Row(text, "Trades", metrics.TradeCount.ToString(CultureInfo.InvariantCulture));
        Row(text, "Win rate", Percent(metrics.WinRate));
        Row(text, "Time in market", Percent(metrics.ExposureFraction));
        Row(text, "Liquidations", metrics.LiquidationCount.ToString(CultureInfo.InvariantCulture));
        text.AppendLine();

        Row(text, "Gross profit", Money(metrics.GrossProfit));
        Row(text, "Gross loss", Money(-metrics.GrossLoss));
        Row(text, "Fees paid", Money(-metrics.TotalFees));
        Row(text, "Funding paid", Money(-metrics.TotalFunding));

        decimal costs = metrics.TotalFees + metrics.TotalFunding;
        if (costs > 0m && metrics.StartingBalance > 0m)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  Costs consumed {Percent(costs / metrics.StartingBalance)} of the starting balance.");
        }

        return text.ToString();

        static void Row(StringBuilder text, string label, string value) =>
            text.AppendLine(CultureInfo.InvariantCulture, $"  {label,-18} {value,16}");
    }

    internal static async Task WriteLedgerAsync(
        BacktestResult result, string path, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        StringBuilder csv = new();
        csv.AppendLine("opened_at,closed_at,side,quantity,entry,exit,gross_pnl,fees,funding,net_pnl,reason");

        foreach (TradeRecord trade in result.Trades)
        {
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{trade.OpenedAt:O},{trade.ClosedAt:O},{trade.Side},{trade.Quantity},{trade.EntryPrice},"
                + $"{trade.ExitPrice},{trade.GrossPnl},{trade.Fees},{trade.Funding},{trade.NetPnl},{trade.Reason}");
        }

        await File.WriteAllTextAsync(path, csv.ToString(), cancellationToken);
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    private static string Percent(decimal value) => value.ToString("P2", CultureInfo.InvariantCulture);
}
