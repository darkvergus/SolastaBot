using System.Security.Cryptography;
using SolastaBot.Data.Chain;

namespace SolastaBot.Tests.Chain;

public sealed class CollectorDatasetReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"solasta-reader-{Guid.NewGuid():N}");

    public CollectorDatasetReaderTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        Directory.Delete(directory, true);
    }

    [Fact]
    public async Task RestartDuplicatesKeepTheFirstAdmissionDecision()
    {
        await WriteAsync([ChainFixture.LaunchJson(110m), ChainFixture.LaunchJson(100m, false, 0m)], [ChainFixture.PollJson(115m)]);
        CollectorDataset result = await new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DuplicateLaunches);
        Assert.False(Assert.Single(result.Launches).Admitted);
        Assert.Empty(result.Observations);
        Assert.Equal(2, result.LaunchFile.Records);
    }

    [Fact]
    public async Task ExactRepeatedPollIsCountedOnceAndInputsAreFingerprinted()
    {
        await WriteAsync([ChainFixture.LaunchJson()], [ChainFixture.PollJson(105m), ChainFixture.PollJson(105m)]);
        CollectorDataset result = await new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DuplicatePolls);
        Assert.Single(result.Observations["test-mint"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "polls.jsonl"), TestContext.Current.CancellationToken))), result.PollFile.Sha256);
    }

    [Fact]
    public async Task ConflictingDuplicatePollFailsWithTheSourceLine()
    {
        await WriteAsync([ChainFixture.LaunchJson()], [ChainFixture.PollJson(105m), ChainFixture.PollJson(105m, 18m)]);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken));

        Assert.Contains("line 2", exception.Message);
    }

    [Fact]
    public async Task TruncatedRecordsAreNotSilentlyDropped()
    {
        await WriteAsync([ChainFixture.LaunchJson()], ["{\"mint\":"]);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() => new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken));

        Assert.Contains("polls.jsonl, line 1", exception.Message);
    }

    [Fact]
    public async Task ScientificNotationAndNumericStringsRemainDecimal()
    {
        string poll = ChainFixture.PollJson(105m).Replace("9000000000", "\"9E9\"", StringComparison.Ordinal);
        await WriteAsync([ChainFixture.LaunchJson()], [poll]);
        CollectorDataset result = await new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken);

        Assert.Equal(9_000_000_000m, Assert.Single(result.Observations["test-mint"]).VirtualQuoteReserves);
    }

    [Fact]
    public async Task AdmittedLaunchMustHaveANonzeroProbability()
    {
        await WriteAsync([ChainFixture.LaunchJson(probability: 0m)], []);

        await Assert.ThrowsAsync<InvalidDataException>(() => new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingReservesStayUnknownAndAnErrorIsRetained()
    {
        await WriteAsync([ChainFixture.LaunchJson()], ["{\"mint\":\"test-mint\",\"t\":105,\"error\":\"http429\"}"]);
        CollectorDataset result = await new CollectorDatasetReader().ReadAsync(directory, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(result.Observations["test-mint"]).VirtualQuoteReserves);
        Assert.Equal("http429", Assert.Single(result.Observations["test-mint"]).Error);
    }

    private async Task WriteAsync(string[] launches, string[] polls)
    {
        await File.WriteAllLinesAsync(Path.Combine(directory, "launches.jsonl"), launches);
        await File.WriteAllLinesAsync(Path.Combine(directory, "polls.jsonl"), polls);
    }
}
