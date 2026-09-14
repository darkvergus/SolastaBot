namespace SolastaBot.Chain.Replay;

public enum ReplayOutcome
{
    NotAdmitted,
    UnsupportedMarket,
    EntryUnavailable,
    MissingEntry,
    TakeProfit,
    StopLoss,
    TimeExit,
    ObservationGap,
    MissingExit,
    Migration,
    InsufficientLiquidity
}
