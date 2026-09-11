namespace SolastaBot.Core.Strategy;

public interface IStrategy
{
    string Name { get; }

    /// <summary>Closed bars needed before <see cref="Evaluate"/> can return anything but warm-up.</summary>
    int WarmupBars { get; }

    /// <summary>Discards all indicator state. Used between backtest folds.</summary>
    void Reset();

    /// <summary>
    /// Consumes exactly one newly closed bar and returns the target exposure. Must be called once
    /// per bar, in ascending time order.
    /// </summary>
    StrategyDecision Evaluate(in MarketSnapshot snapshot);
}
