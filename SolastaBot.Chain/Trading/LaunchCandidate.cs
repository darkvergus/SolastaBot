using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Trading;

public sealed record LaunchCandidate(string Mint, decimal CreatedAt, string? QuoteMint, int? QuoteDecimals, string? Protocol, bool HasTelegram, bool HasTwitter, CurveObservation Curve);
