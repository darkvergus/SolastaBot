namespace SolastaBot.Core.Domain;

/// <summary>
/// The position as the strategy and risk gate see it. <see cref="Quantity"/> is always positive;
/// <see cref="Side"/> carries the direction.
/// </summary>
public sealed record PositionState
{
    public static PositionState Flat { get; } = new();

    public PositionSide Side { get; init; } = PositionSide.Flat;

    public decimal Quantity { get; init; }

    public decimal EntryPrice { get; init; }

    /// <summary>Protective stop price, or zero when there is none.</summary>
    public decimal StopPrice { get; init; }

    public int Leverage { get; init; } = 1;

    public DateTime OpenedAt { get; init; }

    public bool IsOpen => Side != PositionSide.Flat && Quantity > 0m;

    /// <summary>Signed quantity: positive when long, negative when short.</summary>
    public decimal SignedQuantity => Side switch
    {
        PositionSide.Long => Quantity,
        PositionSide.Short => -Quantity,
        _ => 0m
    };

    public decimal UnrealisedPnl(decimal markPrice) => SignedQuantity * (markPrice - EntryPrice);

    public decimal Notional(decimal markPrice) => Quantity * markPrice;
}
