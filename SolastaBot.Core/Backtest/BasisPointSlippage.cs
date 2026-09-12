using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

/// <summary>
/// Constant adverse slip in basis points.
/// </summary>
/// <remarks>
/// Crude on purpose. The point of a slippage model in this system is not to predict execution
/// precisely, it is to find out whether the edge is thick enough to survive execution being worse
/// than hoped. Run every candidate at all three presets; a strategy that only works at
/// <see cref="Low"/> is too thin to trade.
/// </remarks>
public sealed record BasisPointSlippage(string Name, decimal BasisPoints) : ISlippageModel
{
    public static BasisPointSlippage None { get; } = new("none", 0m);

    public static BasisPointSlippage Low { get; } = new("low", 1m);

    public static BasisPointSlippage Medium { get; } = new("medium", 5m);

    public static BasisPointSlippage Harsh { get; } = new("harsh", 15m);

    public static BasisPointSlippage FromName(string name) => name.ToLowerInvariant() switch
    {
        "none" => None,
        "low" => Low,
        "medium" => Medium,
        "harsh" => Harsh,
        _ => throw new ArgumentException($"Unknown slippage profile '{name}'.", nameof(name))
    };

    public decimal Apply(OrderSide side, decimal referencePrice)
    {
        decimal factor = BasisPoints / 10_000m;
        return side == OrderSide.Buy ? referencePrice * (1m + factor) : referencePrice * (1m - factor);
    }
}
