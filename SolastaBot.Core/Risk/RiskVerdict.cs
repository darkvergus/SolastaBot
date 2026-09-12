using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Risk;

/// <summary>
/// The risk gate's answer. <see cref="Quantity"/> of zero always means flat, whatever the outcome.
/// </summary>
public readonly record struct RiskVerdict(PositionSide TargetSide, decimal Quantity, decimal StopPrice, decimal LiquidationPrice, RiskOutcome Outcome, string? Detail)
{
    public static RiskVerdict Flat(RiskOutcome outcome, string? detail = null) => new(PositionSide.Flat, 0m, 0m, 0m, outcome, detail);

    public bool WantsPosition => TargetSide != PositionSide.Flat && Quantity > 0m;
}
