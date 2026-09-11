using SolastaBot.Core.Domain;
using SolastaBot.Core.Execution;
using SolastaBot.Core.Risk;

namespace SolastaBot.Tests.Execution;

public sealed class ExecutionPolicyTests
{
    private static RiskVerdict Wants(PositionSide side, decimal quantity = 1m) =>
        new(side, quantity, 90m, 50m, RiskOutcome.Approved, null);

    private static PositionState Open(PositionSide side) =>
        new() { Side = side, Quantity = 1m, EntryPrice = 100m, StopPrice = 90m };

    [Fact]
    public void AFlatAccountOpensWhenTheGateApprovesADirection()
    {
        PositionPlan plan = new ExecutionPolicy().Plan(Wants(PositionSide.Long), PositionState.Flat);

        Assert.Equal(PositionAction.Open, plan.Action);
        Assert.Equal(PositionSide.Long, plan.Side);
        Assert.Equal(1m, plan.Quantity);
    }

    [Fact]
    public void AnApprovedDirectionMatchingTheOpenPositionDoesNothing()
    {
        PositionPlan plan = new ExecutionPolicy()
            .Plan(Wants(PositionSide.Long, 5m), Open(PositionSide.Long));

        Assert.Equal(PositionAction.None, plan.Action);
    }

    [Fact]
    public void AFlatTargetClosesAnOpenPosition()
    {
        PositionPlan plan = new ExecutionPolicy()
            .Plan(RiskVerdict.Flat(RiskOutcome.NoPosition), Open(PositionSide.Long));

        Assert.Equal(PositionAction.Close, plan.Action);
        Assert.Equal(ExitReason.Signal, plan.CloseReason);
    }

    [Fact]
    public void AnOppositeDirectionReversesThePosition()
    {
        PositionPlan plan = new ExecutionPolicy()
            .Plan(Wants(PositionSide.Short), Open(PositionSide.Long));

        Assert.Equal(PositionAction.Reverse, plan.Action);
        Assert.Equal(PositionSide.Short, plan.Side);
    }

    [Fact]
    public void AHaltedGateClosesThePositionAndSaysWhy()
    {
        PositionPlan plan = new ExecutionPolicy()
            .Plan(RiskVerdict.Flat(RiskOutcome.Halted, "daily loss"), Open(PositionSide.Long));

        Assert.Equal(PositionAction.Close, plan.Action);
        Assert.Equal(ExitReason.RiskHalt, plan.CloseReason);
    }

    [Fact]
    public void TheStoppedDirectionIsRefusedUntilTheTrendFlips()
    {
        ExecutionPolicy policy = new();
        policy.NotifyStopped(PositionSide.Long);

        Assert.Equal(PositionAction.None, policy.Plan(Wants(PositionSide.Long), PositionState.Flat).Action);
        Assert.Equal(PositionSide.Long, policy.BlockedSide);
    }

    [Fact]
    public void AFlatSignalNeitherReEntersNorReleasesTheBlock()
    {
        ExecutionPolicy policy = new();
        policy.NotifyStopped(PositionSide.Long);

        policy.Plan(RiskVerdict.Flat(RiskOutcome.NoPosition), PositionState.Flat);

        Assert.Equal(PositionSide.Long, policy.BlockedSide);
        Assert.Equal(PositionAction.None, policy.Plan(Wants(PositionSide.Long), PositionState.Flat).Action);
    }

    [Fact]
    public void TheOppositeDirectionReleasesTheBlockAndIsTakenImmediately()
    {
        ExecutionPolicy policy = new();
        policy.NotifyStopped(PositionSide.Long);

        PositionPlan plan = policy.Plan(Wants(PositionSide.Short), PositionState.Flat);

        Assert.Equal(PositionAction.Open, plan.Action);
        Assert.Equal(PositionSide.Short, plan.Side);
        Assert.Equal(PositionSide.Flat, policy.BlockedSide);
    }

    [Fact]
    public void OnceReleasedTheOriginalDirectionCanBeTakenAgain()
    {
        ExecutionPolicy policy = new();
        policy.NotifyStopped(PositionSide.Long);
        policy.Plan(Wants(PositionSide.Short), PositionState.Flat);

        Assert.Equal(PositionAction.Open, policy.Plan(Wants(PositionSide.Long), PositionState.Flat).Action);
    }

    [Fact]
    public void ResetClearsTheBlock()
    {
        ExecutionPolicy policy = new();
        policy.NotifyStopped(PositionSide.Short);
        policy.Reset();

        Assert.Equal(PositionSide.Flat, policy.BlockedSide);
        Assert.Equal(PositionAction.Open, policy.Plan(Wants(PositionSide.Short), PositionState.Flat).Action);
    }

    [Fact]
    public void AZeroQuantityApprovalNeverOpens()
    {
        PositionPlan plan = new ExecutionPolicy()
            .Plan(Wants(PositionSide.Long, 0m), PositionState.Flat);

        Assert.Equal(PositionAction.None, plan.Action);
    }
}
