using SolastaBot.Core.Domain;
using SolastaBot.Core.Indicators;

namespace SolastaBot.Core.Strategy;

/// <summary>
/// Trend following on the separation between a fast and a slow exponential moving average, entered
/// only once that separation clears a band, exited at the plain cross, and never reversed directly.
/// </summary>
/// <remarks>
/// This is the answer to the two costs that killed <see cref="EmaCrossStrategy"/>, recorded in
/// <c>docs/m4-gate.md</c>: turnover, and reversing on every cross.
/// <para>
/// Entry and exit are deliberately at different thresholds. Opening needs the fast average a full
/// band beyond the slow one; closing happens the moment they cross back. The gap between those two
/// thresholds is a dead zone the strategy sits flat in, so a cross that immediately un-crosses costs
/// one exit rather than an exit plus an opposing entry. A reversal is still possible, but only by
/// passing through flat and then clearing the band on the other side, which takes at least one more
/// bar and a real move.
/// </para>
/// <para>
/// The separation is measured as a fraction of the slow average rather than in quote currency so
/// that one parameter means the same thing at 20,000 and at 100,000.
/// </para>
/// <para>
/// As in the strategy this replaces, ADX gates entries only. Once positioned, a temporary dip in
/// trend strength during a genuine trend must not churn the position.
/// </para>
/// </remarks>
public sealed class TrendBandStrategy : BarSequencedStrategy
{
    private readonly TrendBandOptions options;
    private readonly decimal entryBand;
    private ExponentialMovingAverage fast;
    private ExponentialMovingAverage slow;
    private AverageTrueRange atr;
    private AverageDirectionalIndex adx;

    public TrendBandStrategy(TrendBandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
        entryBand = options.EntryBandBasisPoints / 10_000m;
        fast = new(options.FastPeriod);
        slow = new(options.SlowPeriod);
        atr = new(options.AtrPeriod);
        adx = new(options.TrendPeriod);
    }

    public override string Name => "trend-band";

    /// <summary>
    /// The slow EMA and the ADX both need warming; ADX costs roughly two of its periods because its
    /// directional series only begins on the second bar and the index smooths those readings again.
    /// </summary>
    public override int WarmupBars => Math.Max(Math.Max(options.SlowPeriod, options.AtrPeriod + 1), options.TrendPeriod * 2 + 1);

    protected override void OnReset()
    {
        fast = new(options.FastPeriod);
        slow = new(options.SlowPeriod);
        atr = new(options.AtrPeriod);
        adx = new(options.TrendPeriod);
    }

    protected override StrategyDecision OnCandle(in MarketSnapshot snapshot)
    {
        Candle candle = snapshot.Candle;
        fast.Add(candle.Close);
        slow.Add(candle.Close);
        atr.Add(candle);
        adx.Add(candle);

        if (!fast.IsReady || !slow.IsReady || !atr.IsReady || !adx.IsReady || atr.Value <= 0m || slow.Value <= 0m)
        {
            return StrategyDecision.Flat(DecisionReason.Warmup);
        }

        decimal stopDistance = atr.Value * options.StopAtrMultiple;
        decimal separation = (fast.Value - slow.Value) / slow.Value;
        PositionSide held = snapshot.Position.Side;

        // Exits are resolved before entries, so a bar that ends one side and would begin the other
        // goes flat instead of reversing. The opposing entry must wait for a later bar.
        if (held == PositionSide.Long)
        {
            return separation > 0m ? new(PositionSide.Long, stopDistance, DecisionReason.Hold) : StrategyDecision.Flat(DecisionReason.ExitOnCross);
        }

        if (held == PositionSide.Short)
        {
            return separation < 0m ? new(PositionSide.Short, stopDistance, DecisionReason.Hold) : StrategyDecision.Flat(DecisionReason.ExitOnCross);
        }

        PositionSide wanted = Wanted(separation);

        if (wanted == PositionSide.Flat)
        {
            return StrategyDecision.Flat(DecisionReason.InsideBand);
        }

        if (adx.Value < options.MinimumTrendStrength)
        {
            return StrategyDecision.Flat(DecisionReason.TrendTooWeak);
        }

        return new(wanted, stopDistance, wanted == PositionSide.Long ? DecisionReason.EnterLong : DecisionReason.EnterShort);
    }

    private PositionSide Wanted(decimal separation)
    {
        if (separation >= entryBand)
        {
            return PositionSide.Long;
        }

        if (separation <= -entryBand)
        {
            return options.AllowShorts ? PositionSide.Short : PositionSide.Flat;
        }

        return PositionSide.Flat;
    }
}
