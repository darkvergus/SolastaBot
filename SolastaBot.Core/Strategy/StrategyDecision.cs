using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Strategy;

/// <summary>Why a strategy arrived at its target exposure. Recorded against every order for audit.</summary>
public enum DecisionReason
{
    Warmup,
    Hold,
    TrendTooWeak,
    EnterLong,
    EnterShort,
    ExitOnCross,
    ReverseOnCross
}

/// <summary>
/// A target exposure, not an order. The strategy says which way it wants to face and how far away
/// the protective stop belongs; turning that into sized, rounded, filter-compliant orders is the
/// job of the risk gate and the router.
/// </summary>
/// <param name="StopDistance">Distance from entry to the protective stop, in quote currency.</param>
public readonly record struct StrategyDecision(
    PositionSide TargetSide,
    decimal StopDistance,
    DecisionReason Reason)
{
    public static StrategyDecision Flat(DecisionReason reason) => new(PositionSide.Flat, 0m, reason);
}
