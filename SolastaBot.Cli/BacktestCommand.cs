using System.CommandLine;
using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Data.Integrity;
using SolastaBot.Data.Market;
using SolastaBot.Data.Storage;

namespace SolastaBot.Cli;

internal static class BacktestCommand
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
        Option<decimal> balance = new("--balance")
        {
            Description = "Starting wallet balance in quote currency.",
            DefaultValueFactory = _ => 10_000m
        };
        Option<int> fast = new("--fast") { Description = "Fast EMA period.", DefaultValueFactory = _ => 21 };
        Option<int> slow = new("--slow") { Description = "Slow EMA period.", DefaultValueFactory = _ => 55 };
        Option<int> atr = new("--atr") { Description = "ATR period.", DefaultValueFactory = _ => 14 };
        Option<decimal> stop = new("--stop-atr")
        {
            Description = "Stop distance as a multiple of ATR.",
            DefaultValueFactory = _ => 2.5m
        };
        Option<decimal> trend = new("--min-adx")
        {
            Description = "ADX floor required before opening.",
            DefaultValueFactory = _ => 20m
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
        Option<bool> longOnly = new("--long-only") { Description = "Never take the short side." };
        Option<bool> noFunding = new("--no-funding")
        {
            Description = "Exclude funding. For diagnosis only; never for a result you intend to act on."
        };
        Option<string?> ledger = new("--ledger") { Description = "Write the trade ledger to this CSV path." };

        Command backtest = new("backtest", "Run the trend strategy over stored history.");
        foreach (Option option in new Option[]
                 {
                     symbol, interval, from, to, database, slippage, balance,
                     fast, slow, atr, stop, trend, risk, leverage, longOnly, noFunding, ledger
                 })
        {
            backtest.Add(option);
        }

        backtest.SetAction(async (result, cancellationToken) =>
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
                Console.Error.WriteLine(
                    $"Only {candles.Count} bars for {contract} {bars.Code} in that range. Run 'solasta data pull' first.");
                return 1;
            }

            IntegrityReport integrity = SeriesIntegrity.Inspect(candles, bars.Duration);
            if (!integrity.IsClean)
            {
                Console.Error.WriteLine($"Refusing to run: {integrity.Describe()}");
                Console.Error.WriteLine("A backtest over a broken series reports a number that means nothing.");
                return 2;
            }

            IReadOnlyList<FundingEvent> funding =
                await store.ReadFundingAsync(contract, start, end, cancellationToken);

            BacktestResult outcome = new BacktestEngine().Run(new BacktestRequest
            {
                Instrument = Instrument.BtcUsdtPerpetual with { Symbol = contract.ToUpperInvariant() },
                Candles = candles,
                Funding = funding,
                Strategy = new EmaCrossStrategy(new EmaCrossOptions
                {
                    FastPeriod = result.GetValue(fast),
                    SlowPeriod = result.GetValue(slow),
                    AtrPeriod = result.GetValue(atr),
                    TrendPeriod = result.GetValue(atr),
                    MinimumTrendStrength = result.GetValue(trend),
                    StopAtrMultiple = result.GetValue(stop),
                    AllowShorts = !result.GetValue(longOnly)
                }),
                Risk = new RiskGate(new RiskOptions
                {
                    RiskFractionPerTrade = result.GetValue(risk),
                    MaxLeverage = result.GetValue(leverage)
                }),
                Options = new BacktestOptions
                {
                    StartingBalance = result.GetValue(balance),
                    Fees = FeeSchedule.BinanceUsdFutures,
                    Slippage = BasisPointSlippage.FromName(result.GetRequiredValue(slippage)),
                    ApplyFunding = !result.GetValue(noFunding)
                }
            });

            Console.WriteLine(BacktestReport.Render(outcome, integrity, funding.Count));

            string? ledgerPath = result.GetValue(ledger);
            if (!string.IsNullOrWhiteSpace(ledgerPath))
            {
                await BacktestReport.WriteLedgerAsync(outcome, ledgerPath, cancellationToken);
                Console.WriteLine($"Trade ledger written to {ledgerPath}");
            }

            return 0;
        });

        return backtest;
    }
}
