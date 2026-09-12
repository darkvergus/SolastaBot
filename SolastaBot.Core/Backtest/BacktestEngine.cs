using System;
using System.Collections.Generic;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Execution;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;

namespace SolastaBot.Core.Backtest;

/// <summary>
/// Event-driven perpetual-futures backtester.
/// </summary>
/// <remarks>
/// The ordering inside each bar is the whole point of this class, so it is stated explicitly:
/// <list type="number">
/// <item>Any order decided on the previous close fills at this bar's open, never at the close of the
/// bar that produced the signal. That one rule is what keeps look-ahead out of the results.</item>
/// <item>Funding settles against whatever position is held after that fill.</item>
/// <item>The protective stop is tested against the bar's range. A bar that gapped straight past the
/// stop fills at the open instead, because that is what actually happens.</item>
/// <item>Liquidation is tested only on what survived the stop.</item>
/// <item>The strategy then sees the bar as closed and decides for the next one.</item>
/// </list>
/// Where the bar's path is ambiguous the adverse reading is taken: a bar whose range spans the stop
/// is assumed to have hit it, because assuming otherwise flatters the result and cannot be checked
/// against bar data.
/// <para>
/// The engine is deterministic. It reads no clock and draws no random numbers, so identical inputs
/// always produce identical ledgers, which is asserted by a test.
/// </para>
/// </remarks>
public sealed class BacktestEngine
{
    public BacktestResult Run(BacktestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Instrument);
        ArgumentNullException.ThrowIfNull(request.Candles);
        ArgumentNullException.ThrowIfNull(request.Strategy);
        ArgumentNullException.ThrowIfNull(request.Risk);
        request.Options.Validate();

        IReadOnlyList<Candle> candles = request.Candles;
        if (candles.Count < 2)
        {
            throw new ArgumentException("A backtest needs at least two bars.", nameof(request));
        }

        Session session = new(request.Options.StartingBalance);
        RiskLedger ledger = new(request.Risk.Options);
        ExecutionPolicy policy = new();
        request.Strategy.Reset();

        TimeSpan interval = candles[1].OpenTime - candles[0].OpenTime;
        RiskVerdict pending = RiskVerdict.Flat(RiskOutcome.NoPosition);
        int fundingIndex = 0;

        for (int index = 0; index < candles.Count; index++)
        {
            Candle bar = candles[index];
            DateTime barEnd = index + 1 < candles.Count ? candles[index + 1].OpenTime : bar.OpenTime + interval;

            ledger.Observe(bar.OpenTime, session.MarkToMarket(bar.Open));

            ApplyPlan(request, session, ledger, policy.Plan(pending, session.Position), bar.Open, bar.OpenTime);

            if (request.Options.ApplyFunding)
            {
                SettleFunding(request, session, bar, barEnd, ref fundingIndex);
            }

            ApplyStop(request, session, ledger, policy, bar);
            ApplyLiquidation(request, session, ledger, bar);

            MarketSnapshot snapshot = new(request.Instrument, bar, session.Position);
            StrategyDecision decision = request.Strategy.Evaluate(snapshot);
            AccountState account = new(session.Wallet, session.Position.UnrealisedPnl(bar.Close));
            pending = request.Risk.Evaluate(decision, snapshot, account, ledger);

            session.EquityCurve.Add(new(barEnd, session.MarkToMarket(bar.Close), session.Wallet));
            if (session.Position.IsOpen)
            {
                session.BarsInPosition++;
            }
        }

        if (session.Position.IsOpen)
        {
            Candle last = candles[^1];
            Close(request, session, ledger, last.Close, last.OpenTime + interval, ExitReason.EndOfData);
        }

        return new(StrategyName: request.Strategy.Name, Symbol: request.Instrument.Symbol, SlippageProfile: request.Options.Slippage.Name, From: candles[0].OpenTime,
            To: candles[^1].OpenTime + interval, Trades: session.Trades, EquityCurve: session.EquityCurve, Metrics: BacktestMetrics.Compute(request.Options.StartingBalance,
                session.EquityCurve, session.Trades, session.BarsInPosition));
    }

    private void ApplyPlan(BacktestRequest request, Session session, RiskLedger ledger, in PositionPlan plan, decimal price, DateTime time)
    {
        switch (plan.Action)
        {
            case PositionAction.None:
                return;

            case PositionAction.Close:
                Close(request, session, ledger, price, time, plan.CloseReason);
                return;

            case PositionAction.Reverse:
                Close(request, session, ledger, price, time, plan.CloseReason);
                Open(request, session, plan, price, time);
                return;

            case PositionAction.Open:
                Open(request, session, plan, price, time);
                return;

            default:
                throw new InvalidOperationException($"Unhandled position action {plan.Action}.");
        }
    }

    private static void Open(BacktestRequest request, Session session, in PositionPlan plan, decimal price, DateTime time)
    {
        OrderSide side = plan.Side == PositionSide.Long ? OrderSide.Buy : OrderSide.Sell;
        decimal fill = request.Options.Slippage.Apply(side, price);
        decimal notional = plan.Quantity * fill;
        decimal fee = request.Options.Fees.TakerFee(notional);

        session.Wallet -= fee;
        session.OpenTradeFees = fee;
        session.OpenTradeFunding = 0m;
        session.LiquidationPrice = plan.LiquidationPrice;
        session.Position = new()
        {
            Side = plan.Side,
            Quantity = plan.Quantity,
            EntryPrice = fill,
            StopPrice = plan.StopPrice,
            Leverage = EffectiveLeverage(notional, session.Wallet),
            OpenedAt = time
        };
    }

    private static void Close(BacktestRequest request, Session session, RiskLedger ledger, decimal price, DateTime time, ExitReason reason)
    {
        PositionState position = session.Position;
        if (!position.IsOpen)
        {
            return;
        }

        OrderSide side = position.Side == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy;
        decimal fill = request.Options.Slippage.Apply(side, price);
        decimal gross = position.SignedQuantity * (fill - position.EntryPrice);
        decimal fee = request.Options.Fees.TakerFee(position.Quantity * fill);

        session.Wallet += gross - fee;

        TradeRecord record = new(Symbol: request.Instrument.Symbol, Side: position.Side, OpenedAt: position.OpenedAt, ClosedAt: time, Quantity: position.Quantity, EntryPrice: position.EntryPrice,
            ExitPrice: fill, GrossPnl: gross, Fees: session.OpenTradeFees + fee, Funding: session.OpenTradeFunding, Reason: reason);

        session.Trades.Add(record);
        ledger.RecordTrade(record);
        session.Position = PositionState.Flat;
        session.LiquidationPrice = 0m;
        session.OpenTradeFees = 0m;
        session.OpenTradeFunding = 0m;
    }

    /// <summary>
    /// Charges every funding settlement falling inside this bar against the position held after the
    /// opening fill. A positive rate means longs pay shorts.
    /// </summary>
    /// <remarks>
    /// The settlement is priced at the bar's open. On the hourly and four-hourly timeframes this is
    /// exact, because funding lands on those boundaries; on daily bars it approximates three
    /// settlements at the day's opening price.
    /// </remarks>
    private static void SettleFunding(BacktestRequest request, Session session, in Candle bar, DateTime barEnd, ref int fundingIndex)
    {
        IReadOnlyList<FundingEvent> events = request.Funding;

        while (fundingIndex < events.Count && events[fundingIndex].Time < bar.OpenTime)
        {
            fundingIndex++;
        }

        while (fundingIndex < events.Count && events[fundingIndex].Time < barEnd)
        {
            FundingEvent settlement = events[fundingIndex];
            fundingIndex++;

            if (!session.Position.IsOpen)
            {
                continue;
            }

            decimal payment = session.Position.SignedQuantity * bar.Open * settlement.Rate;
            session.Wallet -= payment;
            session.OpenTradeFunding += payment;
        }
    }

    private static void ApplyStop(BacktestRequest request, Session session, RiskLedger ledger, ExecutionPolicy policy, in Candle bar)
    {
        PositionState position = session.Position;
        if (!position.IsOpen || position.StopPrice <= 0m)
        {
            return;
        }

        if (position.Side == PositionSide.Long)
        {
            if (bar.Low > position.StopPrice)
            {
                return;
            }

            // A bar that opened below the stop gapped through it, so the fill is the open.
            decimal reference = bar.Open < position.StopPrice ? bar.Open : position.StopPrice;
            Close(request, session, ledger, reference, bar.OpenTime, ExitReason.StopLoss);
            policy.NotifyStopped(PositionSide.Long);
            return;
        }

        if (bar.High < position.StopPrice)
        {
            return;
        }

        decimal shortReference = bar.Open > position.StopPrice ? bar.Open : position.StopPrice;
        Close(request, session, ledger, shortReference, bar.OpenTime, ExitReason.StopLoss);
        policy.NotifyStopped(PositionSide.Short);
    }

    private static void ApplyLiquidation(BacktestRequest request, Session session, RiskLedger ledger, in Candle bar)
    {
        if (!session.Position.IsOpen || session.LiquidationPrice <= 0m)
        {
            return;
        }

        bool hit = session.Position.Side == PositionSide.Long ? bar.Low <= session.LiquidationPrice : bar.High >= session.LiquidationPrice;

        if (hit)
        {
            Close(request, session, ledger, session.LiquidationPrice, bar.OpenTime, ExitReason.Liquidation);
        }
    }

    private static int EffectiveLeverage(decimal notional, decimal equity) => equity <= 0m ? 1 : Math.Max(1, (int)Math.Ceiling(notional / equity));

    private sealed class Session(decimal startingBalance)
    {
        internal decimal Wallet = startingBalance;

        internal PositionState Position = PositionState.Flat;

        internal decimal LiquidationPrice;

        internal decimal OpenTradeFees;

        internal decimal OpenTradeFunding;

        internal int BarsInPosition;

        internal List<TradeRecord> Trades { get; } = [];

        internal List<EquityPoint> EquityCurve { get; } = [];

        internal decimal MarkToMarket(decimal markPrice) => Wallet + Position.UnrealisedPnl(markPrice);
    }
}
