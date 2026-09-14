using System.Text.Json;
using Microsoft.Data.Sqlite;
using SolastaBot.Chain.Trading;

namespace SolastaBot.Data.Chain.Trading;

public sealed class PaperStateStore(string databasePath)
{
    private readonly string databasePath = Path.GetFullPath(databasePath);

    public async Task<PaperTradingState> InitialiseAsync(string settingsHash, PaperTradingState initial, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using SqliteConnection connection = Open(false);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Session (Id INTEGER PRIMARY KEY CHECK (Id = 1), SettingsHash TEXT NOT NULL, State TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS SeenLaunches (Mint TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS Events (Sequence INTEGER PRIMARY KEY, Payload TEXT NOT NULL);
            INSERT OR IGNORE INTO Session (Id, SettingsHash, State) VALUES (1, $hash, $state);
            """;
        command.Parameters.AddWithValue("$hash", settingsHash);
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(initial));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "SELECT SettingsHash, State FROM Session WHERE Id = 1";
        command.Parameters.Clear();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != settingsHash)
        {
            throw new InvalidDataException("Paper session settings differ. Use the original settings or a new state directory; balances will not be reset.");
        }

        return JsonSerializer.Deserialize<PaperTradingState>(reader.GetString(1)) ?? throw new InvalidDataException("Paper state is invalid.");
    }

    public async Task<HashSet<string>> FindSeenAsync(IEnumerable<string> mints, CancellationToken cancellationToken)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        await using SqliteConnection connection = Open(true);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM SeenLaunches WHERE Mint = $mint";
        SqliteParameter parameter = command.Parameters.Add("$mint", SqliteType.Text);
        foreach (string mint in mints)
        {
            parameter.Value = mint;
            if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            {
                seen.Add(mint);
            }
        }

        return seen;
    }

    public async Task SaveAsync(PaperTradingState state, IReadOnlyList<TradingEvent> events, IReadOnlyCollection<string> seenMints, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = Open(false);
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Session SET State = $state WHERE Id = 1";
        command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException("Paper session has not been initialised.");
        }

        command.CommandText = "INSERT OR IGNORE INTO SeenLaunches (Mint) VALUES ($mint)";
        command.Parameters.Clear();
        SqliteParameter mintParameter = command.Parameters.Add("$mint", SqliteType.Text);
        foreach (string mint in seenMints)
        {
            mintParameter.Value = mint;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        command.CommandText = "INSERT INTO Events (Sequence, Payload) VALUES ($sequence, $payload)";
        command.Parameters.Clear();
        SqliteParameter sequenceParameter = command.Parameters.Add("$sequence", SqliteType.Integer);
        SqliteParameter payloadParameter = command.Parameters.Add("$payload", SqliteType.Text);
        foreach (TradingEvent tradingEvent in events)
        {
            sequenceParameter.Value = tradingEvent.Sequence;
            payloadParameter.Value = JsonSerializer.Serialize(tradingEvent);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        transaction.Commit();
    }

    public async Task<PaperTradingState> ReadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = Open(true);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM Session WHERE Id = 1";
        string json = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidDataException("No paper session exists.");
        return JsonSerializer.Deserialize<PaperTradingState>(json) ?? throw new InvalidDataException("Invalid paper state.");
    }

    public async Task<IReadOnlyList<TradingEvent>> ReadEventsAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using SqliteConnection connection = Open(true);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM Events ORDER BY Sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<TradingEvent> events = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(JsonSerializer.Deserialize<TradingEvent>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid trading event."));
        }

        return events;
    }

    private SqliteConnection Open(bool readOnly) => new(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());
}
