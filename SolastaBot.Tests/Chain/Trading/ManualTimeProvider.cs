namespace SolastaBot.Tests.Chain.Trading;

internal sealed class ManualTimeProvider : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(100);

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
