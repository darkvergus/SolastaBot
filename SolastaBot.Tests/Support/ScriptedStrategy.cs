using SolastaBot.Core.Strategy;

namespace SolastaBot.Tests.Support;

/// <summary>
/// A strategy that replays a fixed list of decisions, one per bar.
/// </summary>
/// <remarks>
/// Engine tests need the strategy to be a constant so that every number in the result can be worked
/// out by hand. Driving those tests with a real indicator strategy would only prove that the engine
/// agrees with itself.
/// </remarks>
public sealed class ScriptedStrategy(IReadOnlyList<StrategyDecision> script) : BarSequencedStrategy
{
    private int position;

    public override string Name => "scripted";

    public override int WarmupBars => 0;

    protected override void OnReset() => position = 0;

    protected override StrategyDecision OnCandle(in MarketSnapshot snapshot) =>
        position < script.Count ? script[position++] : StrategyDecision.Flat(DecisionReason.Hold);
}
