using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;

namespace SolastaBot.Core.Execution;

/// <summary>
/// Decides how the live position should change to reach the risk gate's approved target.
/// </summary>
/// <remarks>
/// This sits in Core, and both the backtester and the live trading loop drive it, because any rule
/// about the position's lifecycle that exists in only one of them makes the backtest stop describing
/// live behaviour. Re-entry suppression below is exactly such a rule, and it is the reason this class
/// exists rather than the logic living inside the engine.
/// <para>
/// Two policies are encoded. First, an existing position is never resized: the strategy's target is
/// a direction, and re-sizing on every bar would pay taker fees continuously for no signal. Second,
/// after a stop-out the same direction is refused until the trend has actually flipped the other way
/// and come back. Without that, a stop inside a range re-opens on the very next bar and the strategy
/// pays fees to be stopped out repeatedly by the same chop.
/// </para>
/// </remarks>
public sealed class ExecutionPolicy
{
    private PositionSide blockedSide = PositionSide.Flat;

    /// <summary>The direction currently refused because it was just stopped out.</summary>
    public PositionSide BlockedSide => blockedSide;

    public void Reset() => blockedSide = PositionSide.Flat;

    /// <summary>Called after a stop-out so the same direction is not re-entered immediately.</summary>
    public void NotifyStopped(PositionSide side) => blockedSide = side;

    public PositionPlan Plan(in RiskVerdict verdict, PositionState position)
    {
        ArgumentNullException.ThrowIfNull(position);

        PositionSide target = verdict.TargetSide;

        if (blockedSide != PositionSide.Flat)
        {
            if (target == Opposite(blockedSide))
            {
                blockedSide = PositionSide.Flat;
            }
            else if (target == blockedSide)
            {
                target = PositionSide.Flat;
            }
        }

        if (!position.IsOpen)
        {
            if (target == PositionSide.Flat || verdict.Quantity <= 0m)
            {
                return PositionPlan.None;
            }

            return new PositionPlan(
                PositionAction.Open, target, verdict.Quantity,
                verdict.StopPrice, verdict.LiquidationPrice, ExitReason.None);
        }

        if (target == position.Side)
        {
            return PositionPlan.None;
        }

        ExitReason reason = verdict.Outcome == RiskOutcome.Halted ? ExitReason.RiskHalt : ExitReason.Signal;

        if (target == PositionSide.Flat || verdict.Quantity <= 0m)
        {
            return new PositionPlan(PositionAction.Close, PositionSide.Flat, 0m, 0m, 0m, reason);
        }

        return new PositionPlan(
            PositionAction.Reverse, target, verdict.Quantity,
            verdict.StopPrice, verdict.LiquidationPrice, reason);
    }

    private static PositionSide Opposite(PositionSide side) => side switch
    {
        PositionSide.Long => PositionSide.Short,
        PositionSide.Short => PositionSide.Long,
        _ => PositionSide.Flat
    };
}
