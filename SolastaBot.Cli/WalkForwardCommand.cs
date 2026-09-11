using System.CommandLine;
using System.Globalization;
using System.Text;
using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Data.Integrity;
using SolastaBot.Data.Market;
using SolastaBot.Data.Storage;

namespace SolastaBot.Cli;

internal static class WalkForwardCommand
{
    internal static Command Build()
    {
        Option<string> symbol = CommonOptions.Symbol();
        Option<string> interval = CommonOptions.Interval();
        Option<string> from = CommonOptions.From();
        Option<string> to = CommonOptions.To();
        Option<string> database = CommonOptions.Database();
        Option<string> slippage = new("--slippage")
        {
            Description = "Execution assumption: none, low, medium or harsh.",
            DefaultValueFactory = _ => "medium"
        };
        Option<int> train = new("--train-days")
        {
            Description = "Length of each training window in days.",
            DefaultValueFactory = _ => 180
        };
        Option<int> test = new("--test-days")
        {
            Description = "Length of each out-of-sample window in days.",
            DefaultValueFactory = _ => 60
        };
        Option<decimal> risk = new("--risk")
        {
            Description = "Fraction of equity risked per trade.",
            DefaultValueFactory = _ => 0.005m
        };
        Option<int> leverage = new("--max-leverage")
        {
            Description = "Hard cap on notional divided by equity.",
            DefaultValueFactory = _ => 3
        };
        Option<decimal> balance = new("--balance")
        {
            Description = "Starting wallet balance.",
            DefaultValueFactory = _ => 10_000m
        };

        Command command = new(
            "walk-forward",
            "Choose parameters on past data, measure them on the data that came next.");

        foreach (Option option in new Option[]
                 { symbol, interval, from, to, database, slippage, train, test, risk, leverage, balance })
        {
            command.Add(option);
        }

        command.SetAction(async (result, cancellationToken) =>
        {
            MarketDataStore store = CommonOptions.OpenStore(result.GetRequiredValue(database));
            CandleInterval bars = CommonOptions.ResolveInterval(result.GetRequiredValue(interval));
            string contract = result.GetRequiredValue(symbol);
            DateTime start = CommonOptions.MonthStart(CommonOptions.ParseMonth(result.GetRequiredValue(from), "--from"));
            DateTime end = CommonOptions.MonthEnd(CommonOptions.ParseMonth(result.GetRequiredValue(to), "--to"));

            await store.EnsureCreatedAsync(cancellationToken);
            IReadOnlyList<Candle> candles =
                await store.ReadCandlesAsync(contract, bars, start, end, cancellationToken);

            if (candles.Count < 2)
            {
                Console.Error.WriteLine($"No data for {contract} {bars.Code}. Run 'solasta data pull' first.");
                return 1;
            }

            IntegrityReport integrity = SeriesIntegrity.Inspect(candles, bars.Duration);
            if (!integrity.IsClean)
            {
                Console.Error.WriteLine($"Refusing to run: {integrity.Describe()}");
                return 2;
            }

            IReadOnlyList<FundingEvent> funding =
                await store.ReadFundingAsync(contract, start, end, cancellationToken);

            WalkForwardReport report = new WalkForwardValidator().Run(
                instrument: Instrument.BtcUsdtPerpetual with { Symbol = contract.ToUpperInvariant() },
                candles: candles,
                funding: funding,
                grid: Grid(),
                risk: new RiskOptions
                {
                    RiskFractionPerTrade = result.GetValue(risk),
                    MaxLeverage = result.GetValue(leverage)
                },
                options: new BacktestOptions
                {
                    StartingBalance = result.GetValue(balance),
                    Fees = FeeSchedule.BinanceUsdFutures,
                    Slippage = BasisPointSlippage.FromName(result.GetRequiredValue(slippage))
                },
                walkForward: new WalkForwardOptions
                {
                    TrainWindow = TimeSpan.FromDays(result.GetValue(train)),
                    TestWindow = TimeSpan.FromDays(result.GetValue(test))
                });

            Console.WriteLine(Render(report, contract, result.GetRequiredValue(slippage)));
            return report.Combined.TotalReturn > 0m ? 0 : 3;
        });

        return command;
    }

    /// <summary>
    /// The candidate grid. Deliberately small: a grid with thousands of points will always contain
    /// something that looks excellent on any six-month window, and finding it proves nothing.
    /// </summary>
    private static IReadOnlyList<StrategyCandidate> Grid()
    {
        int[] fasts = [9, 12, 21];
        int[] slows = [26, 55, 100];
        decimal[] stops = [1.5m, 2.5m, 4m];
        decimal[] floors = [15m, 25m];

        List<StrategyCandidate> grid = [];

        foreach (int fast in fasts)
        {
            foreach (int slow in slows)
            {
                if (fast >= slow)
                {
                    continue;
                }

                foreach (decimal stop in stops)
                {
                    foreach (decimal floor in floors)
                    {
                        EmaCrossOptions options = new()
                        {
                            FastPeriod = fast,
                            SlowPeriod = slow,
                            AtrPeriod = 14,
                            TrendPeriod = 14,
                            StopAtrMultiple = stop,
                            MinimumTrendStrength = floor
                        };

                        grid.Add(new StrategyCandidate(
                            $"{fast}/{slow} stop {stop} adx {floor}",
                            () => new EmaCrossStrategy(options)));
                    }
                }
            }
        }

        return grid;
    }

    private static string Render(WalkForwardReport report, string symbol, string slippage)
    {
        StringBuilder text = new();
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"{symbol.ToUpperInvariant()}  walk-forward  slippage={slippage}  {report.Folds.Count} folds");
        text.AppendLine();
        text.AppendLine("  fold  test window            chosen                    train      test");

        foreach (WalkForwardFold fold in report.Folds)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {fold.Index,4}  {fold.TestFrom:yyyy-MM-dd} to {fold.TestTo:yyyy-MM-dd}  "
                + $"{fold.ChosenCandidate,-24}  {fold.Train.TotalReturn,8:P1}  {fold.Test.TotalReturn,8:P1}");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Out-of-sample equity   {report.StartingBalance:N2} to {report.FinalEquity:N2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Out-of-sample return   {report.Combined.TotalReturn:P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Max drawdown           {report.Combined.MaxDrawdown:P2}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Trades                 {report.Combined.TradeCount}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Profitable folds       {report.ProfitableFolds} of {report.Folds.Count}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Parameter changes      {report.ParameterChanges} of {Math.Max(0, report.Folds.Count - 1)} rolls");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"  Fees and funding       {report.Combined.TotalFees + report.Combined.TotalFunding:N2}");

        return text.ToString();
    }
}
