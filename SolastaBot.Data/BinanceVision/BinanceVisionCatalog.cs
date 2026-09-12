using SolastaBot.Data.Market;

namespace SolastaBot.Data.BinanceVision;

/// <summary>
/// Builds paths into Binance's public historical archive.
/// </summary>
/// <remarks>
/// The archive is free, needs no API key, and publishes a SHA-256 companion for every file, which
/// makes it the right source for backtest data. Pulling the same history through the authenticated
/// REST endpoints would take hours of rate-limited paging for the same bytes.
/// </remarks>
public static class BinanceVisionCatalog
{
    public static Uri BaseAddress { get; } = new("https://data.binance.vision/");

    /// <summary>USD-margined futures. Coin-margined contracts live under <c>cm</c> instead.</summary>
    private const string Market = "data/futures/um/monthly";

    public static string MonthlyKlines(string symbol, CandleInterval interval, DateOnly month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(interval);

        string upper = symbol.ToUpperInvariant();
        return $"{Market}/klines/{upper}/{interval.Code}/{upper}-{interval.Code}-{month:yyyy-MM}.zip";
    }

    public static string MonthlyFundingRate(string symbol, DateOnly month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        string upper = symbol.ToUpperInvariant();
        return $"{Market}/fundingRate/{upper}/{upper}-fundingRate-{month:yyyy-MM}.zip";
    }

    public static string Checksum(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        return archivePath + ".CHECKSUM";
    }
}
