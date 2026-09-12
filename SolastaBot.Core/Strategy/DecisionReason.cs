namespace SolastaBot.Core.Strategy;

/// <summary>Why a strategy arrived at its target exposure. Recorded against every order for audit.</summary>
public enum DecisionReason
{
    Warmup,
    Hold,
    TrendTooWeak,

    /// <summary>The averages are too close together to be worth a position, so the strategy stands aside.</summary>
    InsideBand,

    EnterLong,
    EnterShort,
    ExitOnCross,
    ReverseOnCross
}