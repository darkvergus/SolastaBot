using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SolastaBot.Host.Portfolio;

public sealed class PortfolioStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FileStream lease;
    private readonly object syncRoot = new();
    private PortfolioState state;
    public string DirectoryPath { get; }
    public PortfolioEngine Engine { get; } = new();

    public PortfolioStore(string directory, UnifiedSettings settings)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        lease = new(Path.Combine(DirectoryPath, "unified.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "portfolio.db") }.ToString());
        try
        {
            connection.Open();
            using SqliteCommand schema = connection.CreateCommand();
            schema.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS Portfolio (Id INTEGER PRIMARY KEY CHECK(Id=1), Revision INTEGER NOT NULL, Json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Events (Sequence INTEGER PRIMARY KEY, Json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Observations (Sequence INTEGER PRIMARY KEY AUTOINCREMENT, At REAL NOT NULL, Json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ObservationTime ON Observations(At);";
            schema.ExecuteNonQuery();
            using SqliteCommand read = connection.CreateCommand();
            read.CommandText = "SELECT Json FROM Portfolio WHERE Id=1";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { settings.Mode, settings.Strategies }))));
            object? saved = read.ExecuteScalar();
            state = saved is string json ? JsonSerializer.Deserialize<PortfolioState>(json)! : PortfolioState.Create(settings);
            if (state.SchemaVersion != 1 || state.ConfigurationHash.Length != 0 && state.ConfigurationHash != hash)
            {
                throw new InvalidOperationException("Configuration differs from the existing journal. Use dashboard versioned changes or an explicitly separate paper state directory.");
            }

            state.ConfigurationHash = hash;
            Mutate(_ => { });
        }
        catch
        {
            connection.Dispose();
            lease.Dispose();
            throw;
        }
    }

    public PortfolioState Snapshot()
    {
        lock (syncRoot)
        {
            return Copy(state);
        }
    }

    public void Observe(MarketObservation observation) => Mutate(next => Engine.Apply(next, observation), observation);

    public void Mutate(Action<PortfolioState> apply, MarketObservation? observation = null)
    {
        lock (syncRoot)
        {
            PortfolioState next = Copy(state);
            long previousSequence = state.RecentEvents.LastOrDefault()?.Sequence ?? 0;
            apply(next);
            PortfolioEngine.AssertOwnership(next);
            next.Revision = state.Revision + 1;
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (observation is not null)
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO Observations(At,Json) VALUES($at,$json)";
                insert.Parameters.AddWithValue("$at", (double)observation.At);
                insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(observation));
                insert.ExecuteNonQuery();
            }
            foreach (LedgerEntry entry in next.RecentEvents.Where(entry => entry.Sequence > previousSequence))
            {
                using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO Events(Sequence,Json) VALUES($sequence,$json)";
                insert.Parameters.AddWithValue("$sequence", entry.Sequence);
                insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(entry));
                insert.ExecuteNonQuery();
            }
            next.RecentEvents = [.. next.RecentEvents.TakeLast(100)];
            using SqliteCommand write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO Portfolio(Id,Revision,Json) VALUES(1,$revision,$json) ON CONFLICT(Id) DO UPDATE SET Revision=$revision, Json=$json";
            write.Parameters.AddWithValue("$revision", next.Revision);
            write.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next));
            write.ExecuteNonQuery();
            transaction.Commit();
            state = next;
        }
    }

    public IEnumerable<MarketObservation> ReadObservations(decimal from, decimal to)
    {
        using SqliteConnection reader = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "portfolio.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
        reader.Open();
        using SqliteCommand command = reader.CreateCommand();
        command.CommandText = "SELECT Json FROM Observations WHERE At >= $from AND At < $to ORDER BY Sequence";
        command.Parameters.AddWithValue("$from", (double)from);
        command.Parameters.AddWithValue("$to", (double)to);
        using SqliteDataReader rows = command.ExecuteReader();
        while (rows.Read())
        {
            yield return JsonSerializer.Deserialize<MarketObservation>(rows.GetString(0))!;
        }
    }

    public void Dispose()
    {
        connection.Dispose();
        lease.Dispose();
    }

    private static PortfolioState Copy(PortfolioState original) => JsonSerializer.Deserialize<PortfolioState>(JsonSerializer.Serialize(original))!;
}
