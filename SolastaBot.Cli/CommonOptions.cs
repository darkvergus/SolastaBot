using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SolastaBot.Data.BinanceVision;
using SolastaBot.Data.Market;
using SolastaBot.Data.Storage;

namespace SolastaBot.Cli;

/// <summary>Options and wiring shared by every verb.</summary>
internal static class CommonOptions
{
    internal static Option<string> Symbol() => new("--symbol", "-s")
    {
        Description = "Contract symbol, for example BTCUSDT.",
        DefaultValueFactory = _ => "BTCUSDT"
    };

    internal static Option<string> Interval() => new("--interval", "-i")
    {
        Description = "Bar size: 1m, 5m, 15m, 1h, 4h, 1d and so on.",
        DefaultValueFactory = _ => "1h"
    };

    internal static Option<string> From() => new("--from")
    {
        Description = "First month, as yyyy-MM.",
        Required = true
    };

    internal static Option<string> To() => new("--to")
    {
        Description = "Last month, as yyyy-MM, inclusive.",
        Required = true
    };

    internal static Option<string> Database() => new("--db")
    {
        Description = "Path to the local market database.",
        DefaultValueFactory = _ => Path.Combine("data", "market.db")
    };

    /// <summary>Accepts yyyy-MM, and yyyy-MM-dd for convenience, always normalised to the first of the month.</summary>
    internal static DateOnly ParseMonth(string value, string optionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string[] formats = ["yyyy-MM", "yyyy-M", "yyyy-MM-dd"];
        if (DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
        {
            return new(parsed.Year, parsed.Month, 1);
        }

        throw new ArgumentException($"{optionName} must look like 2024-03, but was '{value}'.", nameof(value));
    }

    internal static DateTime MonthStart(DateOnly month) =>
        new(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static DateTime MonthEnd(DateOnly month) => MonthStart(month).AddMonths(1);

    internal static ILoggerFactory Logging(bool verbose) => LoggerFactory.Create(builder => builder
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            })
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information));

    internal static HttpClient BinanceVisionHttp() => new()
    {
        BaseAddress = BinanceVisionCatalog.BaseAddress,
        Timeout = TimeSpan.FromMinutes(5)
    };

    internal static MarketDataStore OpenStore(string path) => new(path);

    internal static CandleInterval ResolveInterval(string code) => CandleInterval.Parse(code);
}
