using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SolastaBot.Chain.Domain;

namespace SolastaBot.Data.Chain;

public sealed class CollectorDatasetReader
{
    public async Task<CollectorDataset> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        Dictionary<string, LaunchObservation> launches = new(StringComparer.Ordinal);
        Dictionary<string, List<CurveObservation>> polls = new(StringComparer.Ordinal);
        int duplicateLaunches = 0;
        int duplicatePolls = 0;
        int orphanPolls = 0;
        int droppedAdmissions = 0;

        CollectorFileEvidence launchFile = await ReadFileAsync(Path.Combine(directory, "launches.jsonl"), row =>
        {
            string mint = RequiredText(row, "mint");
            decimal seenAt = RequiredNumber(row, "seen_at");
            decimal probability = RequiredNumber(row, "admit_p");
            bool admitted = Boolean(row, "admitted") ?? throw new FormatException("Missing admitted flag.");
            decimal? decimals = Number(row, "quote_decimals");
            if (probability < 0m || probability > 1m || admitted && probability == 0m || seenAt <= 0m || decimals.HasValue && (decimals < 0m || decimals > 18m || decimals != decimal.Truncate(decimals.Value)))
            {
                throw new FormatException("Invalid admission probability, timestamp or quote decimals.");
            }

            LaunchObservation launch = new(mint, seenAt, Text(row, "quote_mint"), decimals.HasValue ? (int)decimals.Value : null, Text(row, "protocol"),
                RequiredText(row, "arm"), probability, admitted);
            if (Boolean(row, "dropped_full") == true)
            {
                droppedAdmissions++;
            }

            if (launches.TryGetValue(mint, out LaunchObservation? existing))
            {
                duplicateLaunches++;
                if (existing.SeenAt <= seenAt)
                {
                    return;
                }
            }

            launches[mint] = launch;
        }, cancellationToken);

        CollectorFileEvidence pollFile = await ReadFileAsync(Path.Combine(directory, "polls.jsonl"), row =>
        {
            string mint = RequiredText(row, "mint");
            decimal observedAt = RequiredNumber(row, "t");
            if (observedAt <= 0m)
            {
                throw new FormatException("Invalid observation timestamp.");
            }

            if (!launches.TryGetValue(mint, out LaunchObservation? launch))
            {
                orphanPolls++;
                return;
            }

            if (!launch.Admitted)
            {
                return;
            }

            CurveObservation observation = new(mint, observedAt, Boolean(row, "complete"), Reserve(row, "virtual_sol_reserves"), Reserve(row, "virtual_token_reserves"),
                Reserve(row, "real_sol_reserves"), Reserve(row, "real_token_reserves"), Text(row, "error"));
            if (!polls.TryGetValue(mint, out List<CurveObservation>? history))
            {
                history = [];
                polls.Add(mint, history);
            }

            if (history.Count > 0 && observedAt <= history[^1].ObservedAt)
            {
                if (observation == history[^1])
                {
                    duplicatePolls++;
                    return;
                }

                throw new FormatException($"Conflicting or out-of-order observations for {mint}.");
            }

            history.Add(observation);
        }, cancellationToken);

        Dictionary<string, IReadOnlyList<CurveObservation>> histories = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<CurveObservation>> history in polls)
        {
            histories.Add(history.Key, history.Value.AsReadOnly());
        }

        return new([.. launches.Values.OrderBy(launch => launch.SeenAt).ThenBy(launch => launch.Mint, StringComparer.Ordinal)], histories,
            launchFile, pollFile, duplicateLaunches, duplicatePolls, orphanPolls, droppedAdmissions);
    }

    private static async Task<CollectorFileEvidence> ReadFileAsync(string path, Action<JsonElement> consume, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo before = new(fullPath);
        long length = before.Length;
        DateTime modified = before.LastWriteTimeUtc;
        await using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, new UTF8Encoding(false, true), true, 65536, true);
        int records = 0;
        while (await reader.ReadLineAsync(cancellationToken) is string line)
        {
            records++;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new FormatException("Expected a JSON object.");
                }

                consume(document.RootElement);
            }
            catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"{fullPath}, line {records}: {exception.Message}", exception);
            }
        }

        stream.Position = 0;
        string checksum = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        FileInfo after = new(fullPath);
        if (length != after.Length || modified != after.LastWriteTimeUtc)
        {
            throw new IOException($"{fullPath} changed during analysis. Analyse a completed copy of the collector files.");
        }

        return new(fullPath, length, checksum, records);
    }

    private static string RequiredText(JsonElement row, string name) => Text(row, name) is { Length: > 0 } value ? value : throw new FormatException($"Missing {name}.");

    private static string? Text(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"Invalid {name}.");
    }

    private static decimal RequiredNumber(JsonElement row, string name) => Number(row, name) ?? throw new FormatException($"Missing {name}.");

    private static decimal? Number(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number) ? number : throw new FormatException($"Invalid {name}.");
    }

    private static decimal? Reserve(JsonElement row, string name)
    {
        decimal? reserve = Number(row, name);
        if (reserve.HasValue && (reserve < 0m || reserve > ulong.MaxValue || reserve != decimal.Truncate(reserve.Value)))
        {
            throw new FormatException($"{name} must be unsigned integer base units.");
        }

        return reserve;
    }

    private static bool? Boolean(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"Invalid {name}.")
        };
    }
}
