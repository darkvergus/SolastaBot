using SolastaBot.Chain.Domain;

namespace SolastaBot.Data.Chain;

public sealed record CollectorDataset(IReadOnlyList<LaunchObservation> Launches, IReadOnlyDictionary<string, IReadOnlyList<CurveObservation>> Observations,
    CollectorFileEvidence LaunchFile, CollectorFileEvidence PollFile, int DuplicateLaunches, int DuplicatePolls, int OrphanPolls, int DroppedAdmissions);
