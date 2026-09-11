using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;

namespace SolastaBot.Core.Backtest;

/// <summary>One parameter set under consideration, named for the report.</summary>
public sealed record StrategyCandidate(string Name, Func<IStrategy> Create);

public sealed record WalkForwardOptions
{
    public TimeSpan TrainWindow { get; init; } = TimeSpan.FromDays(180);

    public TimeSpan TestWindow { get; init; } = TimeSpan.FromDays(60);

    /// <summary>Trades a candidate must make in training before its result is believed.</summary>
    public int MinimumTrainTrades { get; init; } = 8;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TrainWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(TestWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumTrainTrades);
    }
}

public sealed record WalkForwardFold(
    int Index,
    DateTime TrainFrom,
    DateTime TrainTo,
    DateTime TestFrom,
    DateTime TestTo,
    string ChosenCandidate,
    BacktestMetrics Train,
    BacktestMetrics Test);

public sealed record WalkForwardReport(
    IReadOnlyList<WalkForwardFold> Folds,
    decimal StartingBalance,
    decimal FinalEquity,
    IReadOnlyList<TradeRecord> Trades,
    IReadOnlyList<EquityPoint> EquityCurve,
    BacktestMetrics Combined)
{
    public int ProfitableFolds => Folds.Count(fold => fold.Test.TotalReturn > 0m);

    /// <summary>
    /// How often the training window chose a different parameter set than the fold before it.
    /// </summary>
    /// <remarks>
    /// A set that keeps changing is the clearest sign that training is fitting noise. A robust edge
    /// should keep picking roughly the same parameters as the window rolls.
    /// </remarks>
    public int ParameterChanges
    {
        get
        {
            int changes = 0;
            for (int index = 1; index < Folds.Count; index++)
            {
                if (Folds[index].ChosenCandidate != Folds[index - 1].ChosenCandidate)
                {
                    changes++;
                }
            }

            return changes;
        }
    }
}

/// <summary>
/// Rolling train-then-test validation.
/// </summary>
/// <remarks>
/// A single backtest over the whole history tells you what the best parameters would have been if
/// you had known the future. Walk-forward asks the only question that matters instead: choosing
/// parameters using data available at the time, how did they do on the data that came next. The
/// concatenated test windows are the honest estimate, and they are usually far worse than the
/// single-pass result.
/// <para>
/// Each test window warms its indicators from its own opening bars, so the first few dozen bars of
/// every fold produce no trades. This wastes a little tradeable time but avoids letting training
/// data leak into the test through indicator state.
/// </para>
/// </remarks>
public sealed class WalkForwardValidator
{
    public WalkForwardReport Run(
        Instrument instrument,
        IReadOnlyList<Candle> candles,
        IReadOnlyList<FundingEvent> funding,
        IReadOnlyList<StrategyCandidate> grid,
        RiskOptions risk,
        BacktestOptions options,
        WalkForwardOptions walkForward)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(funding);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(walkForward);
        walkForward.Validate();
        options.Validate();

        if (grid.Count == 0)
        {
            throw new ArgumentException("Walk-forward needs at least one candidate.", nameof(grid));
        }

        if (candles.Count < 2)
        {
            throw new ArgumentException("Walk-forward needs at least two bars.", nameof(candles));
        }

        BacktestEngine engine = new();
        List<WalkForwardFold> folds = [];
        List<TradeRecord> trades = [];
        List<EquityPoint> curve = [];

        decimal equity = options.StartingBalance;
        DateTime seriesStart = candles[0].OpenTime;
        DateTime seriesEnd = candles[^1].OpenTime;
        DateTime trainFrom = seriesStart;
        int index = 0;

        while (true)
        {
            DateTime trainTo = trainFrom + walkForward.TrainWindow;
            DateTime testTo = trainTo + walkForward.TestWindow;

            if (testTo > seriesEnd)
            {
                break;
            }

            IReadOnlyList<Candle> trainBars = Slice(candles, trainFrom, trainTo);
            IReadOnlyList<Candle> testBars = Slice(candles, trainTo, testTo);

            if (trainBars.Count < 2 || testBars.Count < 2)
            {
                break;
            }

            StrategyCandidate? chosen = Choose(
                engine, instrument, trainBars, funding, grid, risk, options, equity, walkForward,
                out BacktestMetrics trainMetrics);

            if (chosen is null)
            {
                // No candidate traded enough in training to be worth believing; sit this fold out.
                trainFrom += walkForward.TestWindow;
                index++;
                continue;
            }

            BacktestResult test = engine.Run(new BacktestRequest
            {
                Instrument = instrument,
                Candles = testBars,
                Funding = funding,
                Strategy = chosen.Create(),
                Risk = new RiskGate(risk),
                Options = options with { StartingBalance = equity }
            });

            folds.Add(new WalkForwardFold(
                Index: index,
                TrainFrom: trainFrom,
                TrainTo: trainTo,
                TestFrom: trainTo,
                TestTo: testTo,
                ChosenCandidate: chosen.Name,
                Train: trainMetrics,
                Test: test.Metrics));

            trades.AddRange(test.Trades);
            curve.AddRange(test.EquityCurve);
            equity = test.Metrics.FinalEquity;

            trainFrom += walkForward.TestWindow;
            index++;
        }

        return new WalkForwardReport(
            Folds: folds,
            StartingBalance: options.StartingBalance,
            FinalEquity: equity,
            Trades: trades,
            EquityCurve: curve,
            Combined: BacktestMetrics.Compute(options.StartingBalance, curve, trades, CountExposure(curve, trades)));
    }

    /// <summary>
    /// Picks the training window's best candidate by risk-adjusted return, ignoring any that did not
    /// trade enough for its figures to mean anything.
    /// </summary>
    private static StrategyCandidate? Choose(
        BacktestEngine engine,
        Instrument instrument,
        IReadOnlyList<Candle> trainBars,
        IReadOnlyList<FundingEvent> funding,
        IReadOnlyList<StrategyCandidate> grid,
        RiskOptions risk,
        BacktestOptions options,
        decimal equity,
        WalkForwardOptions walkForward,
        out BacktestMetrics trainMetrics)
    {
        StrategyCandidate? best = null;
        double bestScore = double.NegativeInfinity;
        trainMetrics = BacktestMetrics.Compute(equity, [], [], 0);

        foreach (StrategyCandidate candidate in grid)
        {
            BacktestResult result = engine.Run(new BacktestRequest
            {
                Instrument = instrument,
                Candles = trainBars,
                Funding = funding,
                Strategy = candidate.Create(),
                Risk = new RiskGate(risk),
                Options = options with { StartingBalance = equity }
            });

            if (result.Metrics.TradeCount < walkForward.MinimumTrainTrades)
            {
                continue;
            }

            double score = Score(result.Metrics);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
                trainMetrics = result.Metrics;
            }
        }

        return best;
    }

    /// <summary>
    /// Return per unit of drawdown. Ranking on raw return alone reliably selects whichever candidate
    /// took the most risk in that particular window.
    /// </summary>
    private static double Score(BacktestMetrics metrics)
    {
        double drawdown = Math.Max(0.01d, (double)metrics.MaxDrawdown);
        return (double)metrics.TotalReturn / drawdown;
    }

    private static IReadOnlyList<Candle> Slice(IReadOnlyList<Candle> candles, DateTime from, DateTime to)
    {
        List<Candle> slice = [];
        foreach (Candle candle in candles)
        {
            if (candle.OpenTime >= to)
            {
                break;
            }

            if (candle.OpenTime >= from)
            {
                slice.Add(candle);
            }
        }

        return slice;
    }

    private static int CountExposure(IReadOnlyList<EquityPoint> curve, IReadOnlyList<TradeRecord> trades)
    {
        if (curve.Count == 0 || trades.Count == 0)
        {
            return 0;
        }

        // Approximated from the trades rather than tracked per bar, since folds are stitched together.
        TimeSpan held = TimeSpan.Zero;
        foreach (TradeRecord trade in trades)
        {
            held += trade.ClosedAt - trade.OpenedAt;
        }

        TimeSpan span = curve[^1].Time - curve[0].Time;
        return span <= TimeSpan.Zero ? 0 : (int)(curve.Count * (held.TotalSeconds / span.TotalSeconds));
    }
}
