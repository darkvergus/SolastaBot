using Microsoft.Extensions.Logging;
using SolastaBot.Core.Domain;
using SolastaBot.Data.BinanceVision;
using SolastaBot.Data.Integrity;
using SolastaBot.Data.Market;
using SolastaBot.Data.Storage;

namespace SolastaBot.Data;

public sealed record DownloadSummary(
    string Symbol,
    string Interval,
    DateOnly From,
    DateOnly To,
    int MonthsPulled,
    int MonthsSkipped,
    IReadOnlyList<DateOnly> MonthsUnavailable,
    int BarsStored,
    int FundingStored,
    IntegrityReport Integrity);

/// <summary>
/// Pulls a range of monthly archives into the local store and reports on what arrived.
/// </summary>
/// <remarks>
/// A month already held in full is skipped, so an interrupted pull can be re-run without
/// re-downloading gigabytes. Months Binance does not publish are recorded rather than ignored: a
/// symbol that did not exist yet and a download that quietly failed look identical in the database,
/// and only the summary distinguishes them.
/// </remarks>
public sealed class HistoryDownloader(
    BinanceVisionClient client,
    MarketDataStore store,
    ILogger<HistoryDownloader> logger)
{
    public async Task<DownloadSummary> PullAsync(
        string symbol,
        CandleInterval interval,
        DateOnly from,
        DateOnly to,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(interval);

        if (to < from)
        {
            throw new ArgumentException($"The end month {to:yyyy-MM} precedes the start {from:yyyy-MM}.", nameof(to));
        }

        await store.EnsureCreatedAsync(cancellationToken);

        List<DateOnly> unavailable = [];
        int pulled = 0;
        int skipped = 0;
        int bars = 0;
        int funding = 0;

        for (DateOnly month = Normalise(from); month <= Normalise(to); month = month.AddMonths(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTime monthStart = new(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime monthEnd = monthStart.AddMonths(1);

            if (!force && await IsMonthCompleteAsync(symbol, interval, monthStart, monthEnd, cancellationToken))
            {
                skipped++;
                continue;
            }

            IReadOnlyList<Candle>? candles =
                await client.GetMonthlyKlinesAsync(symbol, interval, month, cancellationToken);

            if (candles is null)
            {
                unavailable.Add(month);
                continue;
            }

            bars += await store.UpsertCandlesAsync(symbol, interval, candles, cancellationToken);

            IReadOnlyList<FundingEvent>? settlements =
                await client.GetMonthlyFundingAsync(symbol, month, cancellationToken);

            if (settlements is not null)
            {
                funding += await store.UpsertFundingAsync(symbol, settlements, cancellationToken);
            }

            pulled++;
        }

        DateTime rangeStart = new(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime rangeEnd = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

        IReadOnlyList<Candle> stored =
            await store.ReadCandlesAsync(symbol, interval, rangeStart, rangeEnd, cancellationToken);
        IntegrityReport integrity = SeriesIntegrity.Inspect(stored, interval.Duration);

        logger.LogInformation(
            "Pulled {Pulled} months, skipped {Skipped}, {Unavailable} unavailable. {Integrity}",
            pulled, skipped, unavailable.Count, integrity.Describe());

        return new DownloadSummary(
            Symbol: symbol.ToUpperInvariant(),
            Interval: interval.Code,
            From: Normalise(from),
            To: Normalise(to),
            MonthsPulled: pulled,
            MonthsSkipped: skipped,
            MonthsUnavailable: unavailable,
            BarsStored: bars,
            FundingStored: funding,
            Integrity: integrity);
    }

    /// <summary>A month counts as held only when every slot the cadence implies is already present.</summary>
    private async Task<bool> IsMonthCompleteAsync(
        string symbol, CandleInterval interval, DateTime monthStart, DateTime monthEnd,
        CancellationToken cancellationToken)
    {
        long expected = (monthEnd - monthStart).Ticks / interval.Duration.Ticks;
        int held = await store.CountCandlesAsync(symbol, interval, monthStart, monthEnd, cancellationToken);
        return held >= expected;
    }

    private static DateOnly Normalise(DateOnly month) => new(month.Year, month.Month, 1);
}
