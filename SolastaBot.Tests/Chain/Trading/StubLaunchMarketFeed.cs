using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain.Solana.Interfaces;

namespace SolastaBot.Tests.Chain.Trading;

internal sealed class StubLaunchMarketFeed : ILaunchMarketFeed
{
    internal int LaunchReads { get; private set; }
    internal int CurveReads { get; private set; }

    public Task<IReadOnlyList<LaunchCandidate>> ReadLaunchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LaunchReads++;
        IReadOnlyList<LaunchCandidate> launches = [TradingFixture.Launch(Now())];
        return Task.FromResult(launches);
    }

    public Task<CurveObservation> ReadCurveAsync(string mint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurveReads++;
        return Task.FromResult(TradingFixture.Curve(Now(), mint));
    }

    private static decimal Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000m;
}
