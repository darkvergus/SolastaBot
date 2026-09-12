using System.Globalization;
using Microsoft.Data.Sqlite;
using SolastaBot.Core.Domain;
using SolastaBot.Data.Market;

namespace SolastaBot.Data.Storage;

/// <summary>
/// Local SQLite store for downloaded bars and funding settlements.
/// </summary>
/// <remarks>
/// Prices are stored as text, not as SQLite's REAL. REAL is a double, and a double cannot hold
/// 0.1 exactly; a price that round-trips through one comes back subtly different and then fails the
/// exchange's tick filter by a fraction of a cent. Text round-trips a decimal exactly, and the cost
/// of parsing is irrelevant next to an order the exchange rejects.
/// <para>
/// The primary keys make re-downloading a month idempotent, so an interrupted pull can simply be run
/// again.
/// </para>
/// </remarks>
public sealed class MarketDataStore(string databasePath)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public string DatabasePath => databasePath;

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS candles (
                symbol    TEXT    NOT NULL,
                interval  TEXT    NOT NULL,
                open_time INTEGER NOT NULL,
                open      TEXT    NOT NULL,
                high      TEXT    NOT NULL,
                low       TEXT    NOT NULL,
                close     TEXT    NOT NULL,
                volume    TEXT    NOT NULL,
                PRIMARY KEY (symbol, interval, open_time)
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS funding (
                symbol TEXT    NOT NULL,
                time   INTEGER NOT NULL,
                rate   TEXT    NOT NULL,
                PRIMARY KEY (symbol, time)
            ) WITHOUT ROWID;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> UpsertCandlesAsync(string symbol, CandleInterval interval, IReadOnlyList<Candle> candles, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentNullException.ThrowIfNull(candles);

        if (candles.Count == 0)
        {
            return 0;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO candles (symbol, interval, open_time, open, high, low, close, volume)
            VALUES ($symbol, $interval, $openTime, $open, $high, $low, $close, $volume)
            ON CONFLICT (symbol, interval, open_time) DO UPDATE SET
                open = excluded.open, high = excluded.high, low = excluded.low,
                close = excluded.close, volume = excluded.volume;
            """;

        SqliteParameter symbolParameter = command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        SqliteParameter intervalParameter = command.Parameters.AddWithValue("$interval", interval.Code);
        SqliteParameter openTime = command.Parameters.Add("$openTime", SqliteType.Integer);
        SqliteParameter open = command.Parameters.Add("$open", SqliteType.Text);
        SqliteParameter high = command.Parameters.Add("$high", SqliteType.Text);
        SqliteParameter low = command.Parameters.Add("$low", SqliteType.Text);
        SqliteParameter close = command.Parameters.Add("$close", SqliteType.Text);
        SqliteParameter volume = command.Parameters.Add("$volume", SqliteType.Text);
        _ = symbolParameter;
        _ = intervalParameter;

        foreach (Candle candle in candles)
        {
            openTime.Value = ToUnixMilliseconds(candle.OpenTime);
            open.Value = Text(candle.Open);
            high.Value = Text(candle.High);
            low.Value = Text(candle.Low);
            close.Value = Text(candle.Close);
            volume.Value = Text(candle.Volume);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return candles.Count;
    }

    public async Task<int> UpsertFundingAsync(string symbol, IReadOnlyList<FundingEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
        {
            return 0;
        }

        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO funding (symbol, time, rate) VALUES ($symbol, $time, $rate)
            ON CONFLICT (symbol, time) DO UPDATE SET rate = excluded.rate;
            """;

        command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        SqliteParameter time = command.Parameters.Add("$time", SqliteType.Integer);
        SqliteParameter rate = command.Parameters.Add("$rate", SqliteType.Text);

        foreach (FundingEvent settlement in events)
        {
            time.Value = ToUnixMilliseconds(settlement.Time);
            rate.Value = Text(settlement.Rate);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return events.Count;
    }

    public async Task<IReadOnlyList<Candle>> ReadCandlesAsync(string symbol, CandleInterval interval, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT open_time, open, high, low, close, volume FROM candles
            WHERE symbol = $symbol AND interval = $interval AND open_time >= $from AND open_time < $to
            ORDER BY open_time;
            """;
        command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        command.Parameters.AddWithValue("$interval", interval.Code);
        command.Parameters.AddWithValue("$from", ToUnixMilliseconds(from));
        command.Parameters.AddWithValue("$to", ToUnixMilliseconds(to));

        List<Candle> candles = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candles.Add(new(OpenTime: FromUnixMilliseconds(reader.GetInt64(0)), Open: Number(reader.GetString(1)), High: Number(reader.GetString(2)),
                Low: Number(reader.GetString(3)), Close: Number(reader.GetString(4)), Volume: Number(reader.GetString(5))));
        }

        return candles;
    }

    public async Task<IReadOnlyList<FundingEvent>> ReadFundingAsync(string symbol, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT time, rate FROM funding
            WHERE symbol = $symbol AND time >= $from AND time < $to
            ORDER BY time;
            """;
        command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        command.Parameters.AddWithValue("$from", ToUnixMilliseconds(from));
        command.Parameters.AddWithValue("$to", ToUnixMilliseconds(to));

        List<FundingEvent> events = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new(FromUnixMilliseconds(reader.GetInt64(0)), Number(reader.GetString(1))));
        }

        return events;
    }

    /// <summary>Bars already held for a symbol and interval, so a pull can skip months it has.</summary>
    public async Task<int> CountCandlesAsync(string symbol, CandleInterval interval, DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM candles
            WHERE symbol = $symbol AND interval = $interval AND open_time >= $from AND open_time < $to;
            """;
        command.Parameters.AddWithValue("$symbol", symbol.ToUpperInvariant());
        command.Parameters.AddWithValue("$interval", interval.Code);
        command.Parameters.AddWithValue("$from", ToUnixMilliseconds(from));
        command.Parameters.AddWithValue("$to", ToUnixMilliseconds(to));

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static long ToUnixMilliseconds(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMilliseconds(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Number(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
