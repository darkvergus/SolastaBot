using SolastaBot.Core.Backtest;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;
using SolastaBot.Tests.Support;

namespace SolastaBot.Tests.Backtest;

public sealed class WalkForwardTests
{
    private static readonly Candle[] Series = CandleFactory.RandomWalk(6_000, seed: 909, volatility: 0.012m);

    private static RiskOptions Risk { get; } = new()
    {
        RiskFractionPerTrade = 0.01m,
        MaxLeverage = 3,
        DailyLossLimitFraction = 0.05m,
        MaxConsecutiveLosses = 5
    };

    private static BacktestOptions Options { get; } = new()
    {
        StartingBalance = 10_000m,
        Fees = FeeSchedule.Free,
        Slippage = BasisPointSlippage.None
    };

    private static WalkForwardOptions Windows { get; } = new()
    {
        TrainWindow = TimeSpan.FromDays(40),
        TestWindow = TimeSpan.FromDays(20),
        MinimumTrainTrades = 1
    };

    private static StrategyCandidate Candidate(int fast, int slow, decimal stop) =>
        new($"{fast}/{slow}/{stop}", () => new EmaCrossStrategy(new()
        {
            FastPeriod = fast,
            SlowPeriod = slow,
            AtrPeriod = 14,
            TrendPeriod = 14,
            StopAtrMultiple = stop,
            MinimumTrendStrength = 10m
        }));

    private static IReadOnlyList<StrategyCandidate> Grid() => [Candidate(9, 26, 1.5m), Candidate(12, 55, 2.5m), Candidate(21, 55, 4m)];

    private static WalkForwardReport Run(IReadOnlyList<StrategyCandidate>? grid = null, WalkForwardOptions? windows = null) =>
        new WalkForwardValidator().Run(Instrument.BtcUsdtPerpetual, Series, [], grid ?? Grid(), Risk, Options, windows ?? Windows);

    [Fact]
    public void FoldsAreProducedAndRollForwardByTheTestWindow()
    {
        WalkForwardReport report = Run();

        Assert.NotEmpty(report.Folds);

        for (int index = 1; index < report.Folds.Count; index++)
        {
            WalkForwardFold previous = report.Folds[index - 1];
            WalkForwardFold current = report.Folds[index];

            Assert.Equal(previous.TestFrom + Windows.TestWindow, current.TestFrom);
            Assert.Equal(previous.TestTo, current.TestFrom);
        }
    }

    /// <summary>Training always precedes its own test window, which is the whole point.</summary>
    [Fact]
    public void NoFoldIsTestedOnDataItWasTrainedOn()
    {
        foreach (WalkForwardFold fold in Run().Folds)
        {
            Assert.True(fold.TrainTo <= fold.TestFrom, $"Fold {fold.Index} trains past the start of its test window.");
            Assert.True(fold.TrainFrom < fold.TrainTo);
            Assert.True(fold.TestFrom < fold.TestTo);
        }
    }

    [Fact]
    public void EquityCompoundsFromOneFoldIntoTheNext()
    {
        WalkForwardReport report = Run();

        Assert.Equal(Options.StartingBalance, report.Folds[0].Test.StartingBalance);

        for (int index = 1; index < report.Folds.Count; index++)
        {
            Assert.Equal(report.Folds[index - 1].Test.FinalEquity, report.Folds[index].Test.StartingBalance);
        }

        Assert.Equal(report.Folds[^1].Test.FinalEquity, report.FinalEquity);
    }

    [Fact]
    public void EveryChosenCandidateComesFromTheGrid()
    {
        IReadOnlyList<StrategyCandidate> grid = Grid();
        HashSet<string> names = [.. grid.Select(candidate => candidate.Name)];

        foreach (WalkForwardFold fold in Run(grid).Folds)
        {
            Assert.Contains(fold.ChosenCandidate, names);
        }
    }

    [Fact]
    public void ProfitableFoldsAndParameterChangesAreCountedConsistently()
    {
        WalkForwardReport report = Run();

        Assert.Equal(report.Folds.Count(fold => fold.Test.TotalReturn > 0m), report.ProfitableFolds);
        Assert.InRange(report.ParameterChanges, 0, Math.Max(0, report.Folds.Count - 1));
    }

    [Fact]
    public void TheSameInputsProduceTheSameWalkForward()
    {
        WalkForwardReport first = Run();
        WalkForwardReport second = Run();

        Assert.Equal(first.Folds, second.Folds);
        Assert.Equal(first.FinalEquity, second.FinalEquity);
        Assert.Equal(first.Trades, second.Trades);
    }

    [Fact]
    public void ACandidateThatDoesNotTradeEnoughInTrainingIsNeverChosen()
    {
        StrategyCandidate silent = new("silent", () => new EmaCrossStrategy(new()
        {
            FastPeriod = 9,
            SlowPeriod = 26,
            MinimumTrendStrength = 10_000m
        }));

        WalkForwardReport report = Run([silent, Candidate(9, 26, 1.5m)], Windows with { MinimumTrainTrades = 3 });

        Assert.DoesNotContain(report.Folds, fold => fold.ChosenCandidate == "silent");
    }

    [Fact]
    public void AGridWhereNothingTradesEnoughProducesNoFolds()
    {
        StrategyCandidate silent = new("silent", () => new EmaCrossStrategy(new()
        {
            FastPeriod = 9,
            SlowPeriod = 26,
            MinimumTrendStrength = 10_000m
        }));

        WalkForwardReport report = Run([silent], Windows with { MinimumTrainTrades = 1 });

        Assert.Empty(report.Folds);
        Assert.Equal(Options.StartingBalance, report.FinalEquity);
    }

    [Fact]
    public void AnEmptyGridIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Run([]));
    }

    [Fact]
    public void WindowsThatAreNotPositiveAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Run(windows: Windows with { TrainWindow = TimeSpan.Zero }));
    }

    /// <summary>
    /// The out-of-sample trades are the concatenation of the fold test windows, so the combined
    /// figures must agree with the per-fold ones rather than being computed from anything else.
    /// </summary>
    [Fact]
    public void CombinedTradesAreExactlyTheFoldTestTrades()
    {
        WalkForwardReport report = Run();

        Assert.Equal(report.Folds.Sum(fold => fold.Test.TradeCount), report.Combined.TradeCount);
        Assert.Equal(report.Trades.Count, report.Combined.TradeCount);
    }
}
