using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Execution;

public enum PositionAction
{
    None,
    Open,
    Close,
    Reverse
}

/// <summary>What to do to the position, already sized and priced.</summary>
public readonly record struct PositionPlan(
    PositionAction Action,
    PositionSide Side,
    decimal Quantity,
    decimal StopPrice,
    decimal LiquidationPrice,
    ExitReason CloseReason)
{
    public static PositionPlan None { get; } =
        new(PositionAction.None, PositionSide.Flat, 0m, 0m, 0m, ExitReason.None);
}
