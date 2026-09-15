using SolastaBot.Chain.Trading;
using SolastaBot.Core.Domain;

namespace SolastaBot.Host.Portfolio;

public sealed class TrackedMarket
{
    public required LaunchCandidate Launch { get; set; }
    public decimal AdmissionProbability { get; init; }
    public decimal LastObservedAt { get; set; }
    public decimal LastAttemptAt { get; set; }
    public decimal Minute { get; set; } = -1m;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal FirstSampleAt { get; set; }
    public bool Incomplete { get; set; }
    public List<Candle> Bars { get; set; } = [];
}
