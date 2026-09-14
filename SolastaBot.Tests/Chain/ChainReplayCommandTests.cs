using System.Text.Json;
using System.Text.Json.Nodes;
using SolastaBot.Cli;

namespace SolastaBot.Tests.Chain;

public sealed class ChainReplayCommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"solasta-command-{Guid.NewGuid():N}");

    public ChainReplayCommandTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task CommandProducesReproducibleReportsForAllThreeDelays()
    {
        string settings = await WriteInputsAsync();
        string firstOutput = Path.Combine(directory, "first.json");
        string secondOutput = Path.Combine(directory, "second.json");
        int firstExit = await CommandLine.RunAsync(["chain", "replay", "--data", directory, "--settings", settings, "--output", firstOutput, "--compare-delays"]);
        int secondExit = await CommandLine.RunAsync(["chain", "replay", "--data", directory, "--settings", settings, "--output", secondOutput, "--compare-delays"]);

        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        string firstReport = await File.ReadAllTextAsync(firstOutput, TestContext.Current.CancellationToken);
        Assert.Equal(firstReport, await File.ReadAllTextAsync(secondOutput, TestContext.Current.CancellationToken));
        using JsonDocument report = JsonDocument.Parse(firstReport);
        Assert.Equal("NotEvaluated", report.RootElement.GetProperty("Gate").GetString());
        Assert.Equal(3, report.RootElement.GetProperty("Scenarios").GetArrayLength());
    }

    [Fact]
    public async Task OutputCannotOverwriteTheCollectorData()
    {
        string settings = await WriteInputsAsync();
        string input = Path.Combine(directory, "polls.jsonl");
        string original = await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken);
        int exitCode = await CommandLine.RunAsync(["chain", "replay", "--data", directory, "--settings", settings, "--output", input]);

        Assert.Equal(1, exitCode);
        Assert.Equal(original, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
    }

    [Theory, InlineData(true), InlineData(false)]
    public async Task MissingCostsAndMisspelledSettingsCannotSilentlyUseDefaults(bool removeCost)
    {
        string settings = await WriteInputsAsync();
        JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(settings, TestContext.Current.CancellationToken))!.AsObject();

        if (removeCost)
        {
            configuration.Remove("FeeBasisPoints");
        }
        else
        {
            configuration["FeeBasisPoint"] = 0m;
        }

        await File.WriteAllTextAsync(settings, configuration.ToJsonString(), TestContext.Current.CancellationToken);
        string output = Path.Combine(directory, "report.json");
        int exitCode = await CommandLine.RunAsync(["chain", "replay", "--data", directory, "--settings", settings, "--output", output]);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(output));
    }

    private async Task<string> WriteInputsAsync()
    {
        string settings = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settings, JsonSerializer.Serialize(ChainFixture.Options));
        await File.WriteAllLinesAsync(Path.Combine(directory, "launches.jsonl"), [ChainFixture.LaunchJson()]);

        await File.WriteAllLinesAsync(Path.Combine(directory, "polls.jsonl"), [ChainFixture.PollJson(105m), ChainFixture.PollJson(110m, 18m, 500m),
            ChainFixture.PollJson(111m, 18m, 500m)]);

        return settings;
    }
}