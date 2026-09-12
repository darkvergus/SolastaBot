using SolastaBot.Core.Domain;
using SolastaBot.Core.Strategy;

namespace SolastaBot.Core.Risk;

/// <summary>
/// Turns a target exposure into an approved size, or refuses it.
/// </summary>
/// <remarks>
/// This class may only ever shrink or veto what the strategy asked for. It has no path that
/// increases exposure, which is what keeps a bug in a strategy from becoming a bug in position size.
/// Sizing is fixed-fractional: risk a set slice of equity between entry and the protective stop, so
/// a wider stop buys a smaller position and the loss on a stop-out is roughly constant.
/// </remarks>
public sealed class RiskGate
{
    private readonly RiskOptions options;

    public RiskGate(RiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    public RiskOptions Options => options;

    public RiskVerdict Evaluate(in StrategyDecision decision, in MarketSnapshot snapshot, in AccountState account, RiskLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        string? halt = ledger.HaltReason(account.Equity);
        if (halt is not null)
        {
            return RiskVerdict.Flat(RiskOutcome.Halted, halt);
        }

        if (decision.TargetSide == PositionSide.Flat)
        {
            return RiskVerdict.Flat(RiskOutcome.NoPosition);
        }

        if (account.Equity <= 0m)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, "Equity is exhausted.");
        }

        if (decision.StopDistance <= 0m)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, "Stop distance is not positive.");
        }

        decimal entryPrice = snapshot.Candle.Close;
        if (entryPrice <= 0m)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, "Entry price is not positive.");
        }

        decimal budget = account.Equity * options.RiskFractionPerTrade;
        decimal quantity = budget / decision.StopDistance;
        bool reduced = false;

        decimal leverageCap = account.Equity * options.MaxLeverage / entryPrice;
        if (quantity > leverageCap)
        {
            quantity = leverageCap;
            reduced = true;
        }

        decimal liquidationCap = LiquidationCappedQuantity(account.Equity, entryPrice, decision.StopDistance);
        if (liquidationCap <= 0m)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, "No size leaves room between the stop and liquidation.");
        }

        if (quantity > liquidationCap)
        {
            quantity = liquidationCap;
            reduced = true;
        }

        FilterResult filtered = InstrumentFilter.Prepare(snapshot.Instrument, quantity, null, entryPrice);
        if (!filtered.Accepted)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, filtered.Rejection);
        }

        decimal stopPrice = decision.TargetSide == PositionSide.Long ? entryPrice - decision.StopDistance : entryPrice + decision.StopDistance;

        if (stopPrice <= 0m)
        {
            return RiskVerdict.Flat(RiskOutcome.Rejected, "Stop price falls at or below zero.");
        }

        decimal liquidationPrice = LiquidationPrice(decision.TargetSide, entryPrice, filtered.Quantity, account.Equity);

        return new(decision.TargetSide, filtered.Quantity, InstrumentFilter.RoundToTick(stopPrice, snapshot.Instrument.TickSize, decision.TargetSide == PositionSide.Long ? PriceRounding.Down : PriceRounding.Up),
            liquidationPrice, reduced ? RiskOutcome.Reduced : RiskOutcome.Approved, null);
    }

    /// <summary>
    /// Largest quantity whose liquidation still sits at least
    /// <see cref="RiskOptions.LiquidationBufferAtrMultiple"/> stop-widths beyond entry.
    /// </summary>
    /// <remarks>
    /// Under cross margin the whole wallet absorbs the loss, so liquidation arrives after
    /// <c>(equity - maintenanceMargin)</c> of adverse move, which is <c>(equity - q*P*m) / q</c> in
    /// price terms. Requiring that to exceed <c>buffer</c> rearranges to a closed form,
    /// <c>q &lt;= equity / (P*m + buffer)</c>, so the cap needs no search.
    /// </remarks>
    private decimal LiquidationCappedQuantity(decimal equity, decimal entryPrice, decimal stopDistance)
    {
        decimal buffer = stopDistance * options.LiquidationBufferAtrMultiple;
        decimal denominator = entryPrice * options.MaintenanceMarginRate + buffer;
        return denominator <= 0m ? 0m : equity / denominator;
    }

    private decimal LiquidationPrice(PositionSide side, decimal entryPrice, decimal quantity, decimal equity)
    {
        if (quantity <= 0m)
        {
            return 0m;
        }

        decimal maintenance = quantity * entryPrice * options.MaintenanceMarginRate;
        decimal distance = (equity - maintenance) / quantity;
        return side == PositionSide.Long ? Math.Max(0m, entryPrice - distance) : entryPrice + distance;
    }
}
