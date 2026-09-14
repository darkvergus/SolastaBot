namespace SolastaBot.Chain.Domain;

public sealed record LaunchObservation(string Mint, decimal SeenAt, string? QuoteMint, int? QuoteDecimals, string? Protocol, string Arm, decimal AdmissionProbability, bool Admitted);
