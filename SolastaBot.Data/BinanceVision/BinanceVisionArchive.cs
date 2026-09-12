using System.Globalization;
using System.IO.Compression;
using SolastaBot.Core.Domain;

namespace SolastaBot.Data.BinanceVision;

/// <summary>
/// Reads the CSV inside a Binance Vision archive.
/// </summary>
/// <remarks>
/// Two details of the format matter and are both handled here. Older archives have no header row and
/// newer ones do, so the first line is skipped only when it does not begin with a number. Timestamps
/// are epoch milliseconds today, but Binance has changed that unit before, so the magnitude is
/// checked rather than assumed: no millisecond timestamp reaches 1e14 before the year 5138, and
/// microsecond timestamps passed it in 1973.
/// </remarks>
public static class BinanceVisionArchive
{
    private const long MicrosecondThreshold = 100_000_000_000_000L;

    /// <summary>
    /// Columns of the klines CSV:
    /// open_time, open, high, low, close, volume, close_time, quote_volume, count,
    /// taker_buy_volume, taker_buy_quote_volume, ignore.
    /// </summary>
    public static IReadOnlyList<Candle> ReadKlines(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        List<Candle> candles =
        [
            .. ReadRows(archive, minimumFields: 6).Select(fields => new Candle(OpenTime: ToUtc(long.Parse(fields[0], CultureInfo.InvariantCulture)), Open: Number(fields[1]),
                High: Number(fields[2]), Low: Number(fields[3]), Close: Number(fields[4]), Volume: Number(fields[5])))
        ];

        return candles;
    }

    /// <summary>Columns of the funding CSV: calc_time, funding_interval_hours, last_funding_rate.</summary>
    public static IReadOnlyList<FundingEvent> ReadFundingRates(Stream archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        List<FundingEvent> events =
        [
            .. ReadRows(archive, minimumFields: 3).Select(fields => new FundingEvent(Time: ToUtc(long.Parse(fields[0], CultureInfo.InvariantCulture)), Rate: Number(fields[2])))
        ];

        return events;
    }

    private static IEnumerable<string[]> ReadRows(Stream archive, int minimumFields)
    {
        using ZipArchive zip = new(archive, ZipArchiveMode.Read, leaveOpen: true);

        ZipArchiveEntry entry = zip.Entries.Count == 1 ? zip.Entries[0] : throw new InvalidDataException($"Expected exactly one file in the archive but found {zip.Entries.Count}.");

        using Stream content = entry.Open();
        using StreamReader reader = new(content);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            // A header row starts with a letter; a data row starts with a digit.
            if (!char.IsAsciiDigit(line[0]))
            {
                continue;
            }

            string[] fields = line.Split(',');
            if (fields.Length < minimumFields)
            {
                throw new InvalidDataException($"Row in '{entry.Name}' has {fields.Length} fields, fewer than the {minimumFields} required.");
            }

            yield return fields;
        }
    }

    /// <summary>
    /// Parses a numeric field, allowing the exponent form.
    /// </summary>
    /// <remarks>
    /// Binance writes very small numbers in scientific notation, so a funding rate can arrive as
    /// <c>6.7E-7</c>. The default decimal styles reject an exponent, which makes this look like a
    /// parser bug that only appears on certain months.
    /// </remarks>
    private static decimal Number(string field) => decimal.Parse(field, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static DateTime ToUtc(long timestamp) => timestamp >= MicrosecondThreshold ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp / 1000).UtcDateTime
        : DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
}
