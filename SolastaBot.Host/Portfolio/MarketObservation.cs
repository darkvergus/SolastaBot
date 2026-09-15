using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Core.Domain;

namespace SolastaBot.Host.Portfolio;

public sealed record MarketObservation
{
    public required string Source { get; init; }
    public required string Market { get; init; }
    public decimal At { get; init; }
    public decimal ReceivedAt { get; init; }
    public LaunchCandidate? Launch { get; init; }
    public CurveObservation? Curve { get; init; }
    public Candle? Candle { get; init; }
    public string Interval { get; init; } = "";
    public decimal Price { get; init; }
    public decimal? FundingRate { get; init; }
    public Instrument? Instrument { get; init; }
    public bool Warmup { get; init; }
    public bool Pool { get; init; }
    public decimal? ProtocolFeeBasisPoints { get; init; }
    public decimal AdmissionProbability { get; init; } = 1m;
    public bool Admitted { get; init; } = true;
    public string? Error { get; init; }
}
