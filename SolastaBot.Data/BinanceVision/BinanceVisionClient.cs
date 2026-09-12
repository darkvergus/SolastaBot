using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SolastaBot.Core.Domain;
using SolastaBot.Data.Market;

namespace SolastaBot.Data.BinanceVision;

/// <summary>
/// Downloads monthly history from Binance Vision, verifying every file against its published hash.
/// </summary>
/// <remarks>
/// The checksum is verified rather than trusted. A truncated download produces a valid-looking ZIP
/// with fewer bars in it, and a backtest over silently missing data reports a number that looks
/// plausible and means nothing. Failing loudly here is the only cheap place to catch that.
/// </remarks>
public sealed class BinanceVisionClient(HttpClient http, ILogger<BinanceVisionClient> logger)
{
    /// <summary>Returns the month's bars, or null when Binance publishes no archive for that month.</summary>
    public async Task<IReadOnlyList<Candle>?> GetMonthlyKlinesAsync(string symbol, CandleInterval interval, DateOnly month, CancellationToken cancellationToken)
    {
        byte[]? archive = await DownloadVerifiedAsync(
            BinanceVisionCatalog.MonthlyKlines(symbol, interval, month), cancellationToken);

        if (archive is null)
        {
            return null;
        }

        using MemoryStream stream = new(archive);
        IReadOnlyList<Candle> candles = BinanceVisionArchive.ReadKlines(stream);
        logger.LogInformation("Read {Count} {Interval} bars for {Symbol} from {Month:yyyy-MM}.", candles.Count, interval.Code, symbol, month);
        return candles;
    }

    /// <summary>Returns the month's funding settlements, or null when no archive exists.</summary>
    public async Task<IReadOnlyList<FundingEvent>?> GetMonthlyFundingAsync(string symbol, DateOnly month, CancellationToken cancellationToken)
    {
        byte[]? archive = await DownloadVerifiedAsync(BinanceVisionCatalog.MonthlyFundingRate(symbol, month), cancellationToken);

        if (archive is null)
        {
            return null;
        }

        using MemoryStream stream = new(archive);
        IReadOnlyList<FundingEvent> events = BinanceVisionArchive.ReadFundingRates(stream);
        logger.LogInformation("Read {Count} funding settlements for {Symbol} from {Month:yyyy-MM}.", events.Count, symbol, month);
        return events;
    }

    private async Task<byte[]?> DownloadVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await http.GetAsync(path, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Binance Vision has no archive at {Path}.", path);
            return null;
        }

        response.EnsureSuccessStatusCode();
        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        string expected = await ReadChecksumAsync(BinanceVisionCatalog.Checksum(path), cancellationToken);
        string actual = Convert.ToHexString(SHA256.HashData(payload));

        return !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase) ? throw new ArchiveIntegrityException($"'{path}' hashes to {actual} but its published checksum is {expected}.") : payload;
    }

    /// <summary>The companion file holds one line, the hex digest followed by the file name.</summary>
    private async Task<string> ReadChecksumAsync(string path, CancellationToken cancellationToken)
    {
        string body = await http.GetStringAsync(path, cancellationToken);
        string[] parts = body.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length > 0 ? parts[0] : throw new ArchiveIntegrityException($"The checksum file at '{path}' is empty.");
    }
}
