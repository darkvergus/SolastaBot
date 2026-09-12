using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Text;
using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
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
        Option<string> strategy = new("--strategy")
        {
            Description = "Which strategy to validate: trend-band or ema-cross.",
            DefaultValueFactory = _ => "trend-band"
        };
        Option<bool> fixedParameters = new("--fixed")
        {
            Description = "Roll one frozen parameter set through the folds instead of searching a grid."
        };

        Command command = new("walk-forward", "Choose parameters on past data, measure them on the data that came next.");

        foreach (Option option in new Option[]
                 { symbol, interval, from, to, database, slippage, train, test, risk, leverage, balance, strategy, fixedParameters })
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
            IReadOnlyList<Candle> candles = await store.ReadCandlesAsync(contract, bars, start, end, cancellationToken);

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

            IReadOnlyList<FundingEvent> funding = await store.ReadFundingAsync(contract, start, end, cancellationToken);

            WalkForwardReport report = new WalkForwardValidator().Run(
                instrument: Instrument.BtcUsdtPerpetual with { Symbol = contract.ToUpperInvariant() },
                candles: candles,
                funding: funding,
                grid: Grid(result.GetRequiredValue(strategy), result.GetValue(fixedParameters)),
                risk: new()
                {
                    RiskFractionPerTrade = result.GetValue(risk),
                    MaxLeverage = result.GetValue(leverage)
                },
                options: new()
                {
                    StartingBalance = result.GetValue(balance),
                    Fees = FeeSchedule.BinanceUsdFutures,
                    Slippage = BasisPointSlippage.FromName(result.GetRequiredValue(slippage))
                },
                walkForward: new()
                {
                    TrainWindow = TimeSpan.FromDays(result.GetValue(train)),
                    TestWindow = TimeSpan.FromDays(result.GetValue(test))
                });

            Console.WriteLine(Render(report, contract, result.GetRequiredValue(slippage)));
            return report.Combined.TotalReturn > 0m ? 0 : 3;
        });

        return command;
    }

    private static IReadOnlyList<StrategyCandidate> Grid(string strategy, bool frozen) => strategy.ToLowerInvariant() switch
    {
        "trend-band" => frozen ? TrendBandFrozen() : TrendBandGrid(),
        "ema-cross" => EmaCrossGrid(),
        _ => throw new ArgumentException($"Unknown strategy '{strategy}'. Known strategies: trend-band, ema-cross.", nameof(strategy))
    };

    /// <summary>
    /// One candidate, so that nothing is chosen and nothing can be chosen differently next roll.
    /// </summary>
    /// <remarks>
    /// This is the strongest form of the small-grid defence, and it separates two questions a grid
    /// search confuses: whether the strategy earns anything, and whether the search does. A rolling
    /// result that survives with the parameters nailed down is the strategy's.
    /// </remarks>
    private static IReadOnlyList<StrategyCandidate> TrendBandFrozen()
    {
        TrendBandOptions options = new()
        {
            FastPeriod = 21,
            SlowPeriod = 55,
            AtrPeriod = 14,
            TrendPeriod = 14,
            EntryBandBasisPoints = 50m,
            StopAtrMultiple = 2.5m,
            MinimumTrendStrength = 20m
        };

        return [new("21/55 band 50bp frozen", () => new TrendBandStrategy(options))];
    }

    /// <summary>
    /// Six candidates: three speeds crossed with two entry bands.
    /// </summary>
    /// <remarks>
    /// Deliberately far smaller than <see cref="EmaCrossGrid"/>'s fifty-four. That grid is part of
    /// why the strategy it served changed parameters on twenty of twenty-six rolls: a grid wide
    /// enough to contain a winner for every six-month window will find one every time, and the
    /// finding means nothing. The stop multiple and the ADX floor are fixed here rather than
    /// searched, so what walk-forward measures is the speed and the band and not the search.
    /// </remarks>
    private static IReadOnlyList<StrategyCandidate> TrendBandGrid()
    {
        (int Fast, int Slow)[] speeds = [(13, 34), (21, 55), (34, 89)];
        decimal[] bands = [25m, 75m];

        List<StrategyCandidate> grid = [];

        foreach ((int fast, int slow) in speeds)
        {
            grid.AddRange(from band in bands
                let options = new TrendBandOptions
                {
                    FastPeriod = fast,
                    SlowPeriod = slow,
                    AtrPeriod = 14,
                    TrendPeriod = 14,
                    EntryBandBasisPoints = band,
                    StopAtrMultiple = 2.5m,
                    MinimumTrendStrength = 20m
                }
                select new StrategyCandidate($"{fast}/{slow} band {band}bp", () => new TrendBandStrategy(options)));
        }

        return grid;
    }

    /// <summary>
    /// The rejected strategy's candidate grid, kept so that <c>docs/m4-gate.md</c> stays reproducible.
    /// </summary>
    private static IReadOnlyList<StrategyCandidate> EmaCrossGrid()
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
                    grid.AddRange(from floor in floors
                        let options = new EmaCrossOptions
                        {
                            FastPeriod = fast,
                            SlowPeriod = slow,
                            AtrPeriod = 14,
                            TrendPeriod = 14,
                            StopAtrMultiple = stop,
                            MinimumTrendStrength = floor
                        }
                        select new StrategyCandidate($"{fast}/{slow} stop {stop} adx {floor}", () => new EmaCrossStrategy(options)));
                }
            }
        }

        return grid;
    }

    private static string Render(WalkForwardReport report, string symbol, string slippage)
    {
        StringBuilder text = new();
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{symbol.ToUpperInvariant()}  walk-forward  slippage={slippage}  {report.Folds.Count} folds");
        text.AppendLine();
        text.AppendLine("  fold  test window            chosen                    train      test");

        foreach (WalkForwardFold fold in report.Folds)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {fold.Index,4}  {fold.TestFrom:yyyy-MM-dd} to {fold.TestTo:yyyy-MM-dd}  "
                                                          + $"{fold.ChosenCandidate,-24}  {fold.Train.TotalReturn,8:P1}  {fold.Test.TotalReturn,8:P1}");
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"  Out-of-sample equity   {report.StartingBalance:N2} to {report.FinalEquity:N2}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Out-of-sample return   {report.Combined.TotalReturn:P2}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Max drawdown           {report.Combined.MaxDrawdown:P2}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Trades                 {report.Combined.TradeCount}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Profitable folds       {report.ProfitableFolds} of {report.Folds.Count}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Parameter changes      {report.ParameterChanges} of {Math.Max(0, report.Folds.Count - 1)} rolls");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Fees and funding       {report.Combined.TotalFees + report.Combined.TotalFunding:N2}");

        return text.ToString();
    }
}
