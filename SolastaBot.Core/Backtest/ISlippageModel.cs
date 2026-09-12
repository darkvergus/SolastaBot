using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Backtest;

public interface ISlippageModel
{
    string Name { get; }

    /// <summary>Moves a reference price against the taker by the modelled amount.</summary>
    decimal Apply(OrderSide side, decimal referencePrice);
}