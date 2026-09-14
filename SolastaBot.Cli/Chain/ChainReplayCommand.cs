using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;
using SolastaBot.Data.Chain;

namespace SolastaBot.Cli.Chain;

internal static class ChainReplayCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static Command Build()
    {
        Option<string> directory = new("--data") { Description = "Directory containing completed copies of launches.jsonl and polls.jsonl.", Required = true };
        Option<string> settings = new("--settings") { Description = "JSON file declaring costs and the frozen exit rule.", Required = true };
        Option<string> output = new("--output") { Description = "Write settings, input checksums, summaries and every outcome to this JSON file.", Required = true };
        Option<bool> delays = new("--compare-delays") { Description = "Compare entry 1, 5 and 30 seconds after detection, using the same exit rule." };
        Command replay = new("replay", "Replay observed SOL launch curves with explicit execution assumptions.") { directory, settings, output, delays };
        replay.SetAction(async (result, cancellationToken) =>
        {
            try
            {
                string dataPath = Path.GetFullPath(result.GetRequiredValue(directory));
                string settingsPath = Path.GetFullPath(result.GetRequiredValue(settings));
                string outputPath = Path.GetFullPath(result.GetRequiredValue(output));
                string[] inputs = [settingsPath, Path.Combine(dataPath, "launches.jsonl"), Path.Combine(dataPath, "polls.jsonl")];
                if (inputs.Any(path => string.Equals(path, outputPath, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException("Output must not overwrite an input file.");
                }

                string configuration = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                ReplayOptions options = JsonSerializer.Deserialize<ReplayOptions>(configuration, JsonOptions) ?? throw new ArgumentException("Settings must be a JSON object.");
                options.Validate();
                CollectorDataset dataset = await new CollectorDatasetReader().ReadAsync(dataPath, cancellationToken);
                decimal[] entryDelays = result.GetValue(delays) ? [1m, 5m, 30m] : [options.EntryDelaySeconds];
                List<ChainReplayScenario> scenarios = [];
                LaunchReplay engine = new();
                foreach (decimal delay in entryDelays)
                {
                    ReplayOptions scenarioOptions = options with { EntryDelaySeconds = delay };
                    List<ReplayTrade> trades = [];
                    foreach (LaunchObservation launch in dataset.Launches.Where(launch => launch.Admitted))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        IReadOnlyList<CurveObservation> observations = dataset.Observations.TryGetValue(launch.Mint, out IReadOnlyList<CurveObservation>? history) ? history : [];
                        trades.Add(engine.Run(launch, observations, scenarioOptions));
                    }

                    ReplaySummary[] summaries =
                    [
                        .. trades.GroupBy(trade => trade.Arm).OrderBy(group => group.Key, StringComparer.Ordinal)
                            .Select(group => ReplaySummary.Compute(group.Key, [.. group]))
                    ];
                    scenarios.Add(new(scenarioOptions, summaries, trades));
                }

                string[] limitations =
                [
                    "Research simulation only; this report cannot pass the live trading gate.",
                    "Delays start at collector detection, not on-chain creation. Poll timestamps are request times, not confirmed execution times.",
                    "Only explicitly identified SOL pump curves are supported. Post-migration pool execution is unobserved.",
                    "Fees, slippage, transaction and setup costs are user assumptions, not recovered historical charges.",
                    "Fill price impact is simulated at each snapshot; hypothetical trades do not change subsequent historical snapshots.",
                    "Exit signals fill at a later observation. Price moves inside polling gaps and failed transaction probabilities are unobserved.",
                    "Resolved-only returns have selection bias when outcomes are missing. Stress returns write off every unknown entered position plus one exit transaction.",
                    "Arms are separate unweighted samples. Admission probabilities cannot repair unseen launches, capacity drops or restart-dependent sampling.",
                    "Independent fixed-size trades are not a capital-constrained portfolio and do not measure sleeve drawdown or compounded return."
                ];
                string report = JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1,
                    Gate = "NotEvaluated",
                    dataset.LaunchFile,
                    dataset.PollFile,
                    UniqueLaunches = dataset.Launches.Count,
                    dataset.DuplicateLaunches,
                    dataset.DuplicatePolls,
                    dataset.OrphanPolls,
                    dataset.DroppedAdmissions,
                    Limitations = limitations,
                    Scenarios = scenarios
                }, JsonOptions);

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                await File.WriteAllTextAsync(outputPath, report, cancellationToken);
                Console.WriteLine($"Read {dataset.Launches.Count} distinct launches; {dataset.DuplicateLaunches} duplicate launch records; {dataset.DroppedAdmissions} capacity drops; {dataset.OrphanPolls} orphan polls.");
                Console.WriteLine("Gate: NOT EVALUATED. Snapshot replay with assumed costs; returns are in SOL.");
                Console.WriteLine("Resolved-only results omit unknown exits. Stress results write those positions off completely.");
                foreach (ChainReplayScenario scenario in scenarios)
                {
                    Console.WriteLine(FormattableString.Invariant($"\nEntry delay after detection: {scenario.Options.EntryDelaySeconds}s"));
                    foreach (ReplaySummary summary in scenario.Arms)
                    {
                        Console.WriteLine($"{summary.Arm}: admitted {summary.Admitted}, entered {summary.Entered}, resolved {summary.Resolved}, unknown exits {summary.UnknownExits}, missing entries {summary.MissingEntries}, unavailable entries {summary.UnavailableEntries}, unsupported {summary.Unsupported}");
                        Console.WriteLine($"  Resolved median {Percent(summary.ResolvedMedianReturn)}; resolved mean {Percent(summary.ResolvedMeanReturn)}; stress mean {Percent(summary.StressMeanReturn)}");
                    }
                }

                Console.WriteLine($"Full report: {outputPath}");
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or JsonException or OverflowException)
            {
                Console.Error.WriteLine($"Chain replay failed: {exception.Message}");
                return 1;
            }
        });

        return new("chain", "Solana launch research and trading.") { replay, ChainPaperCommand.Build(), ChainConnectedCommand.Build() };
    }

    private static string Percent(decimal? value) => value.HasValue ? value.Value.ToString("P2", CultureInfo.InvariantCulture) : "unknown";
}
