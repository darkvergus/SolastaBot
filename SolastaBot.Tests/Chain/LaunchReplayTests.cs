using System.Text.Json;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;

namespace SolastaBot.Tests.Chain;

public sealed class LaunchReplayTests
{
    [Fact]
    public void HandComputedRoundTripIncludesCurveImpactBothFeesTransactionsAndSetup()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(110m, 18m, 500m), ChainFixture.Curve(111m, 18m, 500m)];
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(100m, CurvePricing.Buy(observations[0], ChainFixture.Options));
        Assert.Equal(ReplayOutcome.TimeExit, result.Outcome);
        Assert.Equal(105m, result.EnteredAt);
        Assert.Equal(110m, result.ExitSignalAt);
        Assert.Equal(111m, result.ExitedAt);
        Assert.Equal(1.04m, result.EntryCostSol);
        Assert.Equal(2.96m, result.ProceedsSol);
        Assert.Equal(1.92m, result.NetPnlSol);
    }

    [Fact]
    public void EntryUsesFirstObservationAtOrAfterDetectionPlusDelay()
    {
        CurveObservation[] observations = [ChainFixture.Curve(104m, 1m), ChainFixture.Curve(106m), ChainFixture.Curve(111m), ChainFixture.Curve(112m)];
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(106m, result.EnteredAt);
        Assert.Equal(100m, CurvePricing.Buy(observations[1], ChainFixture.Options));
    }

    [Fact]
    public void ProfitSignalFillsAtTheLaterPriceEvenWhenItHasCrashed()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(107m, 18m, 500m), ChainFixture.Curve(108m, 1m, 9000m)];
        ReplayOptions options = ChainFixture.Options with { TakeProfitFraction = 1m, HoldSeconds = 60m };
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, options);

        Assert.Equal(ReplayOutcome.TakeProfit, result.Outcome);
        Assert.Equal(107m, result.ExitSignalAt);
        Assert.Equal(108m, result.ExitedAt);
        Assert.True(result.NetPnlSol < 0m);
    }

    [Fact]
    public void EvenZeroExitDelayCannotFillOnTheSignalObservation()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(110m, 18m, 500m)];
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options with { ExitDelaySeconds = 0m });

        Assert.Equal(ReplayOutcome.MissingExit, result.Outcome);
        Assert.Null(result.NetPnlSol);
        Assert.Equal(-1.05m, result.StressPnlSol);
    }

    [Fact]
    public void EntryIsUnknownWhenOnlyALatePriceIsAvailable()
    {
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, [ChainFixture.Curve(136m)], ChainFixture.Options);

        Assert.Equal(ReplayOutcome.MissingEntry, result.Outcome);
        Assert.Null(result.EnteredAt);
        Assert.Null(result.StressPnlSol);
    }

    [Theory,InlineData(false),InlineData(true)]
    public void ErrorsDoNotAllowTheReplayToBridgeAnUnobservedPriceGap(bool includeError)
    {
        List<CurveObservation> observations = [ChainFixture.Curve(105m)];
        if (includeError)
        {
            observations.Add(ChainFixture.Curve(125m) with { Error = "http429" });
        }

        observations.Add(ChainFixture.Curve(136m, 18m, 500m));
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(ReplayOutcome.ObservationGap, result.Outcome);
        Assert.Null(result.NetPnlSol);
    }

    [Fact]
    public void GraduationDoesNotProduceAFillFromStaleCurveReserves()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(110m, 18m, 500m) with { Complete = true }];
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(ReplayOutcome.Migration, result.Outcome);
        Assert.Null(result.NetPnlSol);
        Assert.Equal(-1.05m, result.StressPnlSol);
    }

    [Fact]
    public void VirtualReservesDoNotMakeAnUnfundedSellFill()
    {
        CurveObservation[] observations =
        [
            ChainFixture.Curve(105m),
            ChainFixture.Curve(110m, 18m, 500m),
            ChainFixture.Curve(111m, 18m, 500m) with { RealQuoteReserves = 1m }
        ];
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(ReplayOutcome.InsufficientLiquidity, result.Outcome);
        Assert.Null(result.NetPnlSol);
    }

    [Fact]
    public void BuyCannotExhaustTheCurveAndAssumeUnchangedTradingAfterMigration()
    {
        CurveObservation entry = ChainFixture.Curve(105m) with { RealTokenReserves = 100m };
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch, [entry], ChainFixture.Options);

        Assert.Equal(ReplayOutcome.EntryUnavailable, result.Outcome);
        Assert.Null(result.EnteredAt);
    }

    [Theory,InlineData(null, 9, "pump"),InlineData("other-quote", 9, "pump"),InlineData("11111111111111111111111111111111", 6, "pump"),InlineData("11111111111111111111111111111111", 9, "other")]
    public void MissingOrUnsupportedMarketMetadataCannotBeAssumedToMeanSol(string? quoteMint, int decimals, string protocol)
    {
        LaunchObservation launch = ChainFixture.Launch with { QuoteMint = quoteMint, QuoteDecimals = decimals, Protocol = protocol };
        ReplayTrade result = new LaunchReplay().Run(launch, [ChainFixture.Curve(105m)], ChainFixture.Options);

        Assert.Equal(ReplayOutcome.UnsupportedMarket, result.Outcome);
    }

    [Fact]
    public void NonAdmittedLaunchIsNotSelectedBecauseItsLaterPathLooksGood()
    {
        ReplayTrade result = new LaunchReplay().Run(ChainFixture.Launch with { Admitted = false }, [ChainFixture.Curve(105m)], ChainFixture.Options);

        Assert.Equal(ReplayOutcome.NotAdmitted, result.Outcome);
    }

    [Fact]
    public void ConflictingTimestampsAndWrongMintsAreRejected()
    {
        LaunchReplay engine = new();

        Assert.Throws<ArgumentException>(() => engine.Run(ChainFixture.Launch, [ChainFixture.Curve(105m), ChainFixture.Curve(105m)], ChainFixture.Options));
        Assert.Throws<ArgumentException>(() => engine.Run(ChainFixture.Launch, [ChainFixture.Curve(105m) with { Mint = "other" }], ChainFixture.Options));
    }

    [Fact]
    public void RepeatingTheReplayProducesAnIdenticalLedger()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(110m), ChainFixture.Curve(111m)];
        LaunchReplay engine = new();
        ReplayTrade first = engine.Run(ChainFixture.Launch, observations, ChainFixture.Options);
        ReplayTrade second = engine.Run(ChainFixture.Launch, observations, ChainFixture.Options);

        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
    }

    [Fact]
    public void UnresolvedTradesRemainInTheStressDistribution()
    {
        LaunchReplay engine = new();
        ReplayTrade winner = engine.Run(ChainFixture.Launch, [ChainFixture.Curve(105m), ChainFixture.Curve(110m, 18m, 500m), ChainFixture.Curve(111m, 18m, 500m)], ChainFixture.Options);
        ReplayTrade unfinished = engine.Run(ChainFixture.Launch, [ChainFixture.Curve(105m)], ChainFixture.Options);
        ReplaySummary summary = ReplaySummary.Compute("filtered_sol", [winner, unfinished]);

        Assert.Equal(2, summary.Entered);
        Assert.Equal(1, summary.Resolved);
        Assert.Equal(1, summary.UnknownExits);
        Assert.Equal(0.87m, summary.StressPnlSol);
        Assert.True(summary.StressMeanReturn < summary.ResolvedMeanReturn);
    }

    [Fact]
    public void AdditionalSlippageWorsensAClosedRoundTrip()
    {
        CurveObservation[] observations = [ChainFixture.Curve(105m), ChainFixture.Curve(110m), ChainFixture.Curve(111m)];
        LaunchReplay engine = new();
        ReplayTrade normal = engine.Run(ChainFixture.Launch, observations, ChainFixture.Options);
        ReplayTrade harsh = engine.Run(ChainFixture.Launch, observations, ChainFixture.Options with { SlippageBasisPoints = 500m });

        Assert.True(harsh.NetPnlSol < normal.NetPnlSol);
    }

    [Fact]
    public void InvalidCostsAndUnreachableExitRulesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => (ChainFixture.Options with { FeeBasisPoints = -1m }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (ChainFixture.Options with { SlippageBasisPoints = 10_000m }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (ChainFixture.Options with { HoldSeconds = 0m }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (ChainFixture.Options with { EntrySetupCostSol = -1m }).Validate());
        Assert.Throws<ArgumentException>(() => (ChainFixture.Options with { TradeSizeSol = 0.0000000001m }).Validate());
    }
}
