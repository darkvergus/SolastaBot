using System.CommandLine;
using Microsoft.Extensions.Logging;
using SolastaBot.Core.Domain;
using SolastaBot.Data;
using SolastaBot.Data.BinanceVision;
using SolastaBot.Data.Integrity;
using SolastaBot.Data.Market;
using SolastaBot.Data.Storage;

namespace SolastaBot.Cli;

internal static class DataCommands
{
    internal static Command Build()
    {
        Command data = new("data", "Download and inspect historical market data.")
        {
            BuildPull(),
            BuildCheck()
        };

        return data;
    }

    private static Command BuildPull()
    {
        Option<string> symbol = CommonOptions.Symbol();
        Option<string> interval = CommonOptions.Interval();
        Option<string> from = CommonOptions.From();
        Option<string> to = CommonOptions.To();
        Option<string> database = CommonOptions.Database();
        Option<bool> force = new("--force") { Description = "Re-download months already held." };
        Option<bool> verbose = new("--verbose", "-v") { Description = "Log every request." };

        Command pull = new("pull", "Download monthly bars and funding rates from the Binance public archive.")
        {
            symbol,
            interval,
            from,
            to,
            database,
            force,
            verbose
        };

        pull.SetAction(async (result, cancellationToken) =>
        {
            using ILoggerFactory logging = CommonOptions.Logging(result.GetValue(verbose));
            using HttpClient http = CommonOptions.BinanceVisionHttp();

            BinanceVisionClient client = new(http, logging.CreateLogger<BinanceVisionClient>());
            MarketDataStore store = CommonOptions.OpenStore(result.GetRequiredValue(database));
            HistoryDownloader downloader = new(client, store, logging.CreateLogger<HistoryDownloader>());

            DownloadSummary summary = await downloader.PullAsync(result.GetRequiredValue(symbol), CommonOptions.ResolveInterval(result.GetRequiredValue(interval)),
                CommonOptions.ParseMonth(result.GetRequiredValue(from), "--from"), CommonOptions.ParseMonth(result.GetRequiredValue(to), "--to"),
                result.GetValue(force), cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"{summary.Symbol} {summary.Interval}  {summary.From:yyyy-MM} to {summary.To:yyyy-MM}");
            Console.WriteLine($"  months pulled     {summary.MonthsPulled}");
            Console.WriteLine($"  months already held {summary.MonthsSkipped}");
            Console.WriteLine($"  months unavailable  {summary.MonthsUnavailable.Count}");
            Console.WriteLine($"  bars stored       {summary.BarsStored}");
            Console.WriteLine($"  funding stored    {summary.FundingStored}");
            Console.WriteLine($"  integrity         {summary.Integrity.Describe()}");

            if (summary.MonthsUnavailable.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  No archive published for: {string.Join(", ", summary.MonthsUnavailable.Select(month => month.ToString("yyyy-MM")))}");
            }

            return summary.Integrity.IsClean ? 0 : 2;
        });

        return pull;
    }

    private static Command BuildCheck()
    {
        Option<string> symbol = CommonOptions.Symbol();
        Option<string> interval = CommonOptions.Interval();
        Option<string> from = CommonOptions.From();
        Option<string> to = CommonOptions.To();
        Option<string> database = CommonOptions.Database();

        Command check = new("check", "Report gaps, duplicates and malformed bars in the local store.")
        {
            symbol,
            interval,
            from,
            to,
            database
        };

        check.SetAction(async (result, cancellationToken) =>
        {
            MarketDataStore store = CommonOptions.OpenStore(result.GetRequiredValue(database));
            CandleInterval bars = CommonOptions.ResolveInterval(result.GetRequiredValue(interval));
            DateOnly first = CommonOptions.ParseMonth(result.GetRequiredValue(from), "--from");
            DateOnly last = CommonOptions.ParseMonth(result.GetRequiredValue(to), "--to");

            await store.EnsureCreatedAsync(cancellationToken);

            IReadOnlyList<Candle> candles = await store.ReadCandlesAsync(result.GetRequiredValue(symbol), bars, CommonOptions.MonthStart(first), CommonOptions.MonthEnd(last), cancellationToken);

            IntegrityReport report = SeriesIntegrity.Inspect(candles, bars.Duration);
            Console.WriteLine(report.Describe());

            if (report.MissingSample.Count > 0)
            {
                Console.WriteLine("First missing slots:");
                foreach (DateTime slot in report.MissingSample.Take(10))
                {
                    Console.WriteLine($"  {slot:yyyy-MM-dd HH:mm} UTC");
                }
            }

            return report.IsClean ? 0 : 2;
        });

        return check;
    }
}
