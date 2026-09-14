namespace SolastaBot.Data.Chain;

public sealed record CollectorFileEvidence(string Path, long Bytes, string Sha256, int Records);
