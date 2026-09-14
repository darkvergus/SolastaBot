using System.Text.Json;
using Microsoft.Data.Sqlite;
using SolastaBot.Chain.Execution;

namespace SolastaBot.Data.Chain.Trading;

public sealed class ConnectedStateStore(string path)
{
    public async Task<ConnectedState> OpenAsync(string identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using SqliteConnection connection = Connection();
        await connection.OpenAsync(cancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS ConnectedSession(Id INTEGER PRIMARY KEY CHECK(Id=1), Revision INTEGER NOT NULL, Payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ConnectedEvents(Revision INTEGER PRIMARY KEY, At TEXT NOT NULL, Kind TEXT NOT NULL, Payload TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "INSERT OR IGNORE INTO ConnectedSession VALUES(1,0,$initial); SELECT Payload FROM ConnectedSession WHERE Id=1;";
        command.Parameters.AddWithValue("$initial", JsonSerializer.Serialize(new ConnectedState { Identity = identity }));
        string json = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        ConnectedState state = JsonSerializer.Deserialize<ConnectedState>(json)!;
        if (state.Identity != identity)
        {
            throw new InvalidOperationException("Network, wallet or settings differ from this session.");
        }

        return state;
    }

    public async Task<ConnectedState> SaveAsync(ConnectedState state, string kind, CancellationToken cancellationToken)
    {
        ConnectedState next = state with { Revision = checked(state.Revision + 1) };
        using SqliteConnection connection = Connection();
        await connection.OpenAsync(cancellationToken);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ConnectedSession SET Revision=$next, Payload=$payload WHERE Id=1 AND Revision=$previous;";
        command.Parameters.AddWithValue("$next", next.Revision);
        command.Parameters.AddWithValue("$previous", state.Revision);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(next));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Concurrent session writer detected.");
        }

        command.CommandText = "INSERT INTO ConnectedEvents VALUES($next,$at,$kind,$event);";
        command.Parameters.AddWithValue("$event", JsonSerializer.Serialize(new { next.WalletLamports, next.ReconciliationError, next.Positions, Order = next.Orders.LastOrDefault() is ExecutionOrder last ? last with { Transaction = string.Empty } : null }));
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$kind", kind);
        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
        return next;
    }

    public async Task<string> ReadAsync(bool events, CancellationToken cancellationToken)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = events ? "SELECT json_object('revision',Revision,'at',At,'kind',Kind) FROM ConnectedEvents ORDER BY Revision DESC LIMIT 100;" : "SELECT Payload FROM ConnectedSession WHERE Id=1;";
        using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<string> rows = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, rows);
    }

    private SqliteConnection Connection() => new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
}
