using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Risk;

public enum RiskOutcome
{
    /// <summary>Size approved exactly as derived from the risk budget.</summary>
    Approved,

    /// <summary>Approved at a smaller size than the risk budget alone would allow.</summary>
    Reduced,

    /// <summary>Strategy wants flat, or already is. Nothing to size.</summary>
    NoPosition,

    /// <summary>Trading is halted; any open position must be closed and no new one opened.</summary>
    Halted,

    /// <summary>The trade cannot be expressed within the exchange's filters or the account's equity.</summary>
    Rejected
}

/// <summary>
/// The risk gate's answer. <see cref="Quantity"/> of zero always means flat, whatever the outcome.
/// </summary>
public readonly record struct RiskVerdict(
    PositionSide TargetSide,
    decimal Quantity,
    decimal StopPrice,
    decimal LiquidationPrice,
    RiskOutcome Outcome,
    string? Detail)
{
    public static RiskVerdict Flat(RiskOutcome outcome, string? detail = null) =>
        new(PositionSide.Flat, 0m, 0m, 0m, outcome, detail);

    public bool WantsPosition => TargetSide != PositionSide.Flat && Quantity > 0m;
}
