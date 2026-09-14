using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;

namespace SolastaBot.Exchange.Chain;

public interface ILaunchMarketFeed
{
    Task<IReadOnlyList<LaunchCandidate>> ReadLaunchesAsync(CancellationToken cancellationToken);
    Task<CurveObservation> ReadCurveAsync(string mint, CancellationToken cancellationToken);
}
