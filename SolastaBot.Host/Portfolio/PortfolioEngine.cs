using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;
using SolastaBot.Core.Domain;
using SolastaBot.Core.Risk;
using SolastaBot.Core.Strategy;

namespace SolastaBot.Host.Portfolio;

public sealed class PortfolioEngine
{
    public void Apply(PortfolioState state, MarketObservation observation)
    {
        state.Now = Math.Max(state.Now, Math.Max(observation.At, observation.ReceivedAt));
        Tick(state, state.Now);

        if (observation.Error is not null)
        {
            state.Health[observation.Source] = observation.Error;

            return;
        }

        state.Health[observation.Source] = $"Last observation: {DateTimeOffset.FromUnixTimeMilliseconds((long)(observation.At * 1000m)):u}";
        bool continuationSignal = false;

        if (observation.Launch is not null && observation.Admitted && !state.Markets.ContainsKey(observation.Market))
        {
            state.Markets.Add(observation.Market, new() { Launch = observation.Launch, AdmissionProbability = observation.AdmissionProbability });
        }

        if (observation.Curve is not null && state.Markets.TryGetValue(observation.Market, out TrackedMarket? market))
        {
            continuationSignal = ObserveBar(market, observation);
        }

        MarketObservation remainingLiquidity = observation;

        foreach (StrategyAccount account in state.Strategies.Values.ToArray())
        {
            if (account.TrialCompleted)
            {
                continue;
            }

            if (observation is { Warmup: false, FundingRate: null } && observation.ReceivedAt > observation.At + account.Settings.MaximumQuoteAgeSeconds)
            {
                account.LastDecision = "Delayed observation rejected";

                continue;
            }

            if (observation.At < account.LastObservedAt && observation.FundingRate is null && !observation.Warmup)
            {
                continue;
            }

            ObserveDay(account, observation.At);

            if (account.Settings.Asset == "SOL" && observation.Curve is not null)
            {
                long previousSequence = state.RecentEvents.LastOrDefault()?.Sequence ?? 0;
                MarketObservation executable = account.Settings.Trial ? observation : remainingLiquidity;
                ApplyChain(state, account, executable, continuationSignal);

                if (!account.Settings.Trial)
                {
                    foreach (LedgerEntry fill in state.RecentEvents.Where(entry => entry.Sequence > previousSequence && entry.Kind is "Buy" or "Sell"))
                    {
                        remainingLiquidity = Consume(remainingLiquidity, fill, account.Settings);
                    }
                }
            }
            else if (account.Settings.Asset == "USDT" && observation.Market == account.Settings.Symbol && observation.Source.StartsWith("binance", StringComparison.Ordinal))
            {
                ApplyPerpetual(state, account, observation);
            }

            if (account.Reserved > Math.Max(0m, account.Cash))
            {
                account.Orders.Clear();
                Record(state, account, "CancelEntry", "", 0m, 0m, "Account balance no longer covers reservations");
            }

            ObserveDay(account, state.Now);

            if (account.DailyHalt)
            {
                account.Orders.Clear();

                foreach (PortfolioPosition position in account.Positions)
                {
                    RequestExit(position, state.Now, position.Quantity, "Daily loss limit");
                }
            }

            account.PeakEquity = Math.Max(account.PeakEquity, account.Equity - account.NetContributions + account.Settings.InitialCapital);
            decimal performanceEquity = account.Equity - account.NetContributions + account.Settings.InitialCapital;

            if (account.PeakEquity > 0m)
            {
                account.MaxDrawdown = Math.Max(account.MaxDrawdown, (account.PeakEquity - performanceEquity) / account.PeakEquity);
            }
        }

        AssertOwnership(state);
    }

    public void Tick(PortfolioState state, decimal now)
    {
        state.Now = Math.Max(state.Now, now);

        foreach (StrategyAccount account in state.Strategies.Values.Where(account => !account.TrialCompleted))
        {
            if (account.Settings.Trial)
            {
                if (account.TrialStartedAt == 0m)
                {
                    account.TrialStartedAt = now;
                }

                if (now >= account.TrialStartedAt + 7m * 86400m)
                {
                    account.Paused = true;
                    account.Flatten = true;
                    account.Orders.Clear();

                    if (account.Positions.Count == 0)
                    {
                        account.TrialCompleted = true;
                        account.Candles.Clear();
                        account.EnteredMarkets.Clear();
                        Record(state, account, "TrialCompleted", "", account.RealizedPnl, 0m, "Seven-day paper trial completed; results retained");

                        continue;
                    }
                }
            }

            ObserveDay(account, now);
            account.Orders.RemoveAll(order => now > order.ExpiresAt || EntriesHalted(state, account));

            foreach (PortfolioPosition position in account.Positions)
            {
                if (account.Flatten || account.DailyHalt)
                {
                    RequestExit(position, now, position.Quantity, account.Flatten ? "Manual flatten" : "Daily loss limit");
                }
                else if (account.Settings.Asset == "SOL" && now - position.OpenedAt >= account.Settings.HoldSeconds)
                {
                    RequestExit(position, position.OpenedAt + account.Settings.HoldSeconds, position.Quantity, "Time exit");
                }
            }
        }
    }

    public void ConfigureSplit(PortfolioState state, string strategyId, Dictionary<string, decimal> split)
    {
        StrategyAccount account = state.Strategies[strategyId];

        if (account.Settings.Trial || split.Count == 0 || split.Values.Any(fraction => fraction < 0m || fraction > 1m) || split.Values.Sum() != 1m)
        {
            throw new ArgumentException("Profit shares must total 100%; trial capital is isolated.");
        }

        foreach (string destination in split.Keys)
        {
            if (destination is not ("retain" or "reserve") &&
                (!state.Strategies.TryGetValue(destination, out StrategyAccount? target) || target.Settings.Trial || destination == strategyId))
            {
                throw new ArgumentException("Unknown or ineligible destination; use retain for the source strategy.");
            }
        }

        account.ProfitSplit = new(split);
        Record(state, account, "ProfitSplit", "", 0m, 0m, "Profit split updated");
    }

    public void TransferBudget(PortfolioState state, string sourceId, string targetId, decimal amount)
    {
        StrategyAccount source = state.Strategies[sourceId];
        StrategyAccount target = state.Strategies[targetId];

        if (sourceId == targetId || amount <= 0m || source.Available < amount || source.Settings.Asset != target.Settings.Asset || source.Settings.Trial || target.Settings.Trial)
        {
            throw new ArgumentException("Budget transfers require available funds in the same asset and managed portfolio.");
        }

        Move(source, target, amount);
        Record(state, source, "BudgetTransfer", targetId, amount, 0m, "Explicit budget transfer");
    }

    public void Distribute(PortfolioState state, StrategyAccount account)
    {
        if (account.Settings.Trial || account.ProfitSplit.Count == 0)
        {
            return;
        }

        if (account.Settings.Asset == "USDT" && account.Exposures.Any(exposure => exposure.ClosedAt > account.LastFundingAt))
        {
            return;
        }

        decimal profit = Math.Min(Math.Max(0m, account.RealizedPnl - account.DistributedHighWater), Math.Max(0m, account.Available));

        if (profit <= 0m)
        {
            return;
        }

        foreach (KeyValuePair<string, decimal> share in account.ProfitSplit)
        {
            decimal amount = profit * share.Value;

            if (share.Key == "retain" || amount == 0m)
            {
                continue;
            }

            if (share.Key == "reserve")
            {
                account.Cash -= amount;
                account.NetContributions -= amount;
                account.DayOpeningEquity -= amount;
                state.Reserves[account.Settings.Asset] = state.Reserves.GetValueOrDefault(account.Settings.Asset) + amount;
            }
            else
            {
                StrategyAccount target = state.Strategies[share.Key];

                if (target.Settings.Asset == account.Settings.Asset)
                {
                    Move(account, target, amount);
                }
                else
                {
                    account.Cash -= amount;
                    account.NetContributions -= amount;
                    account.DayOpeningEquity -= amount;
                    string key = $"{account.Settings.Asset}:{share.Key}";
                    state.PendingAllocations[key] = state.PendingAllocations.GetValueOrDefault(key) + amount;
                }
            }

            Record(state, account, "ProfitDistribution", share.Key, amount, 0m, "Settled profit above recovered losses");
        }

        account.DistributedHighWater += profit;
    }

    public void RecordConversion(PortfolioState state, string allocation, decimal sourceAmount, decimal receivedAmount, string reference)
    {
        string[] parts = allocation.Split(':', 2);

        if (parts.Length != 2 || sourceAmount <= 0m || receivedAmount <= 0m || string.IsNullOrWhiteSpace(reference) || reference.Length > 100 ||
            state.PendingAllocations.GetValueOrDefault(allocation) < sourceAmount || !state.Strategies.TryGetValue(parts[1], out StrategyAccount? target) || target.Settings.Trial)
        {
            throw new ArgumentException("Conversion requires an existing pending allocation, positive amounts, and a unique reference.");
        }

        if (!state.ConversionReferences.Add(reference))
        {
            throw new ArgumentException("Conversion reference already recorded.");
        }

        state.PendingAllocations[allocation] -= sourceAmount;
        target.Cash += receivedAmount;
        target.NetContributions += receivedAmount;
        target.DayOpeningEquity += receivedAmount;
        Record(state, target, "PaperConversion", allocation, receivedAmount, sourceAmount, reference);
    }

    private void ApplyChain(PortfolioState state, StrategyAccount account, MarketObservation observation, bool continuationSignal)
    {
        StrategySettings settings = account.Settings;
        CurveObservation curve = observation.Curve!;
        PortfolioPosition? position = account.Positions.FirstOrDefault(holding => holding.Market == observation.Market);
        ReplayOptions pricing = Pricing(settings, observation);

        if (position is not null && observation.At > position.MarkedAt)
        {
            decimal? proceeds = CurvePricing.Sell(curve, position.Quantity, pricing);

            if (proceeds is null)
            {
                position.Mark = 0m;
                account.LastDecision = "Sell unavailable; position retained and marked zero";
                Record(state, account, "Unfilled", position.Market, 0m, position.Quantity, account.LastDecision);

                return;
            }

            position.Mark = proceeds.Value;
            position.MarkedAt = observation.At;
            position.HighestUnitExit = Math.Max(position.HighestUnitExit, (proceeds.Value + settings.TransactionCostSol) / position.Quantity);

            if (position.ExitRequestedAt.HasValue && observation.At >= position.ExitRequestedAt + settings.ExitDelaySeconds)
            {
                decimal quantity = Math.Min(position.ExitQuantity, position.Quantity);
                decimal? partialProceeds = CurvePricing.Sell(curve, quantity, pricing);
                decimal rawQuote = decimal.Floor(curve.VirtualQuoteReserves!.Value * quantity / (curve.VirtualTokenReserves!.Value + quantity));
                decimal fee = decimal.Ceiling(rawQuote * pricing.FeeBasisPoints / 10_000m) / CurvePricing.LamportsPerSol + settings.TransactionCostSol;

                if (partialProceeds.HasValue)
                {
                    Close(state, account, position, quantity, partialProceeds.Value, position.ExitReason, fee, observation.At);
                }

                return;
            }

            if (position.ExitRequestedAt is null)
            {
                if (account.Flatten || account.DailyHalt || observation.At - position.OpenedAt >= settings.HoldSeconds || proceeds <= position.Cost * (1m - settings.StopFraction))
                {
                    RequestExit(position, observation.At, position.Quantity,
                        account.Flatten ? "Manual flatten" :
                        account.DailyHalt ? "Daily loss limit" :
                        observation.At - position.OpenedAt >= settings.HoldSeconds ? "Time exit" : "Stop loss");
                }
                else if (!position.PartialTaken && proceeds >= position.Cost * (1m + settings.TakeProfitFraction))
                {
                    RequestExit(position, observation.At, Math.Max(1m, decimal.Floor(position.Quantity * settings.PartialFraction)), "Partial profit");
                }
                else if (position.PartialTaken && (proceeds.Value + settings.TransactionCostSol) / position.Quantity <= position.HighestUnitExit * (1m - settings.TrailFraction))
                {
                    RequestExit(position, observation.At, position.Quantity, "Trailing exit");
                }
            }

            account.LastDecision = position.ExitRequestedAt.HasValue ? position.ExitReason : "Holding";

            return;
        }

        PortfolioOrder? pending = account.Orders.FirstOrDefault(order => order.Market == observation.Market);

        if (pending is not null)
        {
            if (observation.At > pending.ExpiresAt || EntriesHalted(state, account))
            {
                account.Orders.Remove(pending);
            }
            else if (observation.At >= pending.ExecuteAfter && observation.At > pending.RequestedAt)
            {
                account.Orders.Remove(pending);
                decimal? quantity = CurvePricing.Buy(curve, pricing);

                if (quantity.HasValue && CanOpen(state, account, pending.Reserved, observation.At, observation.Market))
                {
                    decimal mark = CurvePricing.Sell(curve, quantity.Value, pricing) ?? 0m;
                    account.Cash -= pending.Reserved;

                    account.Positions.Add(new()
                    {
                        Market = observation.Market, Quantity = quantity.Value, Cost = pending.Reserved, EntryPrice = pending.Reserved / quantity.Value, OpenedAt = observation.At,
                        Mark = mark, MarkedAt = observation.At, HighestUnitExit = (mark + settings.TransactionCostSol) / quantity.Value
                    });

                    account.TotalFees += settings.TransactionCostSol + settings.SetupCostSol + settings.TradeSizeSol * pricing.FeeBasisPoints / (10_000m + pricing.FeeBasisPoints);
                    Record(state, account, "Buy", observation.Market, pending.Reserved, quantity.Value, "Paper fill from subsequent reserves");
                }
                else
                {
                    account.LastDecision = "Entry unavailable or risk limit";
                }
            }

            return;
        }

        if (!state.Markets.TryGetValue(observation.Market, out TrackedMarket? tracked) || account.EnteredMarkets.Contains(observation.Market))
        {
            return;
        }

        decimal age = observation.At - tracked.Launch.CreatedAt;
        bool supported = tracked.Launch is { QuoteMint: "11111111111111111111111111111111", QuoteDecimals: 9, Protocol: "pump" };
        bool liquid = curve.Error is null && curve is { Complete: false, HasReserves: true } && curve.RealQuoteReserves >= settings.MinimumRealSol * CurvePricing.LamportsPerSol;

        bool signal = settings.Kind == "solana-launch"
            ? supported && liquid && age is >= 0m and <= 60m && tracked.Launch is { HasTelegram: true, HasTwitter: true } &&
              curve.VirtualQuoteReserves > settings.MinimumVirtualSol * CurvePricing.LamportsPerSol
            : supported && liquid && age is >= 300m and <= 10800m && continuationSignal;

        if (!signal)
        {
            return;
        }

        decimal cost = settings.TradeSizeSol + settings.TransactionCostSol + settings.SetupCostSol;

        if (!CanOpen(state, account, cost, observation.At, observation.Market))
        {
            return;
        }

        account.EnteredMarkets.Add(observation.Market);

        account.Orders.Add(new(observation.Market, observation.At, observation.At + settings.EntryDelaySeconds,
            observation.At + settings.EntryDelaySeconds + settings.MaximumQuoteAgeSeconds, cost));

        Record(state, account, "EntrySignal", observation.Market, cost, 0m, settings.Kind);
    }

    private void ApplyPerpetual(PortfolioState state, StrategyAccount account, MarketObservation observation)
    {
        StrategySettings settings = account.Settings;
        PortfolioPosition? position = account.Positions.FirstOrDefault();
        decimal price = observation.Price;

        if (observation.FundingRate is { } rate && observation.At > account.LastFundingAt)
        {
            account.LastFundingAt = observation.At;

            decimal signedQuantity = account.Exposures.Where(exposure => exposure.OpenedAt < observation.At && (exposure.ClosedAt is null || exposure.ClosedAt >= observation.At))
                .Sum(exposure => exposure.Quantity * exposure.Direction);

            if (signedQuantity != 0m)
            {
                decimal funding = signedQuantity * price * rate;
                account.Cash -= funding;
                account.RealizedPnl -= funding;
                account.TotalFunding += funding;

                if (position is not null && position.OpenedAt < observation.At)
                {
                    position.Funding += funding;
                }

                Record(state, account, "Funding", observation.Market, -funding, Math.Abs(signedQuantity),
                    "Actual funding settlement, including positions closed before publication");
            }

            Distribute(state, account);
            account.Exposures.RemoveAll(exposure => exposure.ClosedAt < observation.At);

            return;
        }

        if (!observation.Warmup && price > 0m && observation.Candle is null)
        {
            account.LastObservedAt = observation.At;

            if (position is not null)
            {
                position.Mark = position.Cost + position.Direction * position.Quantity * (price - position.EntryPrice);
                position.MarkedAt = observation.At;
                bool stopped = position.Direction > 0 ? price <= position.StopPrice : price >= position.StopPrice;
                bool liquidated = position.Direction > 0 ? price <= position.LiquidationPrice : price >= position.LiquidationPrice;

                if (liquidated || stopped || account.Flatten || account.DailyHalt)
                {
                    RequestExit(position, observation.At, position.Quantity,
                        liquidated ? "Liquidation" : stopped ? "Stop loss" : account.Flatten ? "Manual flatten" : "Daily loss limit");
                }

                if (position.ExitRequestedAt is { } requested && observation.At > requested)
                {
                    decimal fill = price * (1m - position.Direction * settings.SlippageBasisPoints / 10_000m);
                    decimal fee = position.Quantity * fill * settings.FeeBasisPoints / 10_000m;
                    decimal proceeds = position.Cost + position.Direction * position.Quantity * (fill - position.EntryPrice) - fee;

                    if (position.ExitReason is "Stop loss" or "Liquidation")
                    {
                        account.BlockedDirection = position.Direction;
                    }

                    Close(state, account, position, position.Quantity, proceeds, position.ExitReason, fee, observation.At);
                }

                return;
            }

            PortfolioOrder? order = account.Orders.FirstOrDefault();

            if (order is not null && observation.At > order.RequestedAt)
            {
                account.Orders.Remove(order);

                if (!CanOpen(state, account, order.Reserved, observation.At, observation.Market) || observation.At > order.ExpiresAt || observation.Instrument is null)
                {
                    return;
                }

                decimal fill = price * (1m + order.Direction * settings.SlippageBasisPoints / 10_000m);
                decimal quantity = InstrumentFilter.FloorToStep(order.Reserved / (fill * (1m + settings.FeeBasisPoints / 10_000m)), observation.Instrument.StepSize);
                FilterResult filter = InstrumentFilter.Prepare(observation.Instrument, quantity, null, fill);

                if (!filter.Accepted)
                {
                    return;
                }

                decimal notional = filter.Quantity * fill;
                decimal fee = notional * settings.FeeBasisPoints / 10_000m;

                if (fill - order.Direction * order.StopDistance <= 0m)
                {
                    return;
                }

                account.Cash -= notional + fee;
                account.RealizedPnl -= fee;
                account.TotalFees += fee;

                account.Positions.Add(new()
                {
                    Market = observation.Market, Quantity = filter.Quantity, Cost = notional, EntryPrice = fill, EntryFee = fee, Direction = order.Direction,
                    OpenedAt = observation.At, Mark = notional, MarkedAt = observation.At, StopPrice = fill - order.Direction * order.StopDistance,
                    LiquidationPrice = order.Direction > 0 ? fill * 0.005m : fill * 1.995m
                });

                account.Exposures.Add(new() { Market = observation.Market, Quantity = filter.Quantity, Direction = order.Direction, OpenedAt = observation.At });
                Record(state, account, "Buy", observation.Market, notional + fee, filter.Quantity, order.Direction > 0 ? "Paper long, 1x isolated" : "Paper short, 1x isolated");
            }

            return;
        }

        if (observation.Candle is not { } candle || observation.Interval != settings.Interval || observation.Instrument is null ||
            account.Candles.Count > 0 && candle.OpenTime <= account.Candles[^1].OpenTime)
        {
            return;
        }

        if (!candle.IsWellFormed)
        {
            throw new InvalidDataException("Malformed candle.");
        }

        if (account.Candles.Count > 0 && candle.OpenTime != account.Candles[^1].OpenTime.AddHours(settings.Interval == "1h" ? 1 : 4))
        {
            account.LastDecision = "Candle gap; waiting for backfill";
            account.Orders.Clear();

            return;
        }

        IStrategy strategy = settings.Kind == "binance-trend"
            ? new TrendBandStrategy(settings.Trend)
            : new EmaCrossStrategy(new()
            {
                FastPeriod = settings.Trend.FastPeriod, SlowPeriod = settings.Trend.SlowPeriod, AtrPeriod = settings.Trend.AtrPeriod, TrendPeriod = settings.Trend.TrendPeriod,
                MinimumTrendStrength = settings.Trend.MinimumTrendStrength, StopAtrMultiple = settings.Trend.StopAtrMultiple, AllowShorts = settings.Trend.AllowShorts
            });

        foreach (Candle history in account.Candles)
        {
            MarketSnapshot warmup = new(observation.Instrument, history, PositionState.Flat);
            strategy.Evaluate(warmup);
        }

        account.Candles.Add(candle);

        PositionState held = position is null
            ? PositionState.Flat
            : new()
            {
                Side = position.Direction > 0 ? PositionSide.Long : PositionSide.Short, Quantity = position.Quantity, EntryPrice = position.EntryPrice,
                StopPrice = position.StopPrice, Leverage = 1
            };

        MarketSnapshot snapshot = new(observation.Instrument, candle, held);
        StrategyDecision decision = strategy.Evaluate(snapshot);

        if (observation.Warmup)
        {
            account.LastDecision = $"Loaded {account.Candles.Count} closed bars; waiting for next close";

            return;
        }

        int direction = decision.TargetSide == PositionSide.Long ? 1 : decision.TargetSide == PositionSide.Short ? -1 : 0;

        if (direction != account.BlockedDirection)
        {
            account.BlockedDirection = 0;
        }

        account.LastDecision = decision.Reason.ToString();

        if (position is not null)
        {
            if (direction != position.Direction)
            {
                RequestExit(position, observation.At, position.Quantity, "Strategy exit");
            }

            return;
        }

        if (direction == 0 || direction == account.BlockedDirection || account.Orders.Count != 0)
        {
            return;
        }

        RiskOptions riskOptions = new() { MaxLeverage = 1 };
        RiskLedger ledger = new(riskOptions);
        ledger.Observe(candle.OpenTime, account.Equity);
        RiskVerdict verdict = new RiskGate(riskOptions).Evaluate(decision, snapshot, new(account.Equity, 0m), ledger);

        if (verdict.Quantity <= 0m)
        {
            return;
        }

        decimal reserve = Math.Min(verdict.Quantity * candle.Close * (1m + settings.FeeBasisPoints / 10_000m), account.Equity * settings.MaxPositionFraction);

        if (CanOpen(state, account, reserve, observation.At, observation.Market))
        {
            account.Orders.Add(new(observation.Market, observation.At, observation.At, observation.At + 30m, reserve, direction, decision.StopDistance));
            Record(state, account, "EntrySignal", observation.Market, reserve, 0m, decision.Reason.ToString());
        }
    }

    private void Close(PortfolioState state, StrategyAccount account, PortfolioPosition position, decimal quantity, decimal proceeds, string reason, decimal fee, decimal now)
    {
        decimal portion = quantity / position.Quantity;
        decimal cost = position.Cost * portion;
        decimal pnl = proceeds - cost;
        account.Cash += proceeds;
        account.RealizedPnl += pnl;
        account.TotalFees += fee;
        bool finished = quantity == position.Quantity;
        Record(state, account, "Sell", position.Market, proceeds, quantity, reason);

        if (finished)
        {
            account.Positions.Remove(position);
            account.ClosedTrades++;
            decimal tradePnl = pnl + position.ClosedPortionPnl - position.Funding - position.EntryFee;

            if (tradePnl < 0m)
            {
                account.NegativePnl -= tradePnl;
                account.ConsecutiveLosses++;
            }
            else
            {
                account.PositivePnl += tradePnl;
                account.ConsecutiveLosses = 0;
            }

            foreach (PerpetualExposure exposure in account.Exposures.Where(exposure => exposure.Market == position.Market && exposure.ClosedAt is null))
            {
                exposure.ClosedAt = now;
            }

            Distribute(state, account);
        }
        else
        {
            position.Quantity -= quantity;
            position.Cost -= cost;
            position.ClosedPortionPnl += pnl;
            position.Mark *= 1m - portion;
            position.PartialTaken = true;
            position.ExitRequestedAt = null;
            position.ExitQuantity = 0m;
            position.ExitReason = "";
        }

        ObserveDay(account, now);
    }

    private static bool ObserveBar(TrackedMarket market, MarketObservation observation)
    {
        CurveObservation curve = observation.Curve!;

        if (observation.At <= market.LastObservedAt || !curve.HasReserves || curve.Error is not null || curve.Complete != false)
        {
            return false;
        }

        decimal price = curve.VirtualQuoteReserves!.Value / curve.VirtualTokenReserves!.Value;
        decimal minute = decimal.Floor(observation.At / 60m);
        bool signal = false;

        if (minute != market.Minute)
        {
            bool complete = market.Minute >= 0m && minute == market.Minute + 1m && !market.Incomplete && market.FirstSampleAt - market.Minute * 60m <= 10m &&
                            market.Minute * 60m + 60m - market.LastObservedAt <= 10m;

            if (complete)
            {
                if (market.Bars.Count >= 10)
                {
                    signal = market.Close > market.Bars.TakeLast(10).Max(bar => bar.High);
                }

                market.Bars.Add(new(DateTimeOffset.FromUnixTimeSeconds((long)(market.Minute * 60m)).UtcDateTime, market.Open, market.High, market.Low, market.Close, 0m));

                if (market.Bars.Count > 11)
                {
                    market.Bars.RemoveAt(0);
                }
            }
            else
            {
                market.Bars.Clear();
            }

            market.Minute = minute;
            market.FirstSampleAt = observation.At;
            market.Open = price;
            market.High = price;
            market.Low = price;
            market.Incomplete = false;
        }
        else if (observation.At - market.LastObservedAt > 10m)
        {
            market.Incomplete = true;
        }

        market.Close = price;
        market.High = Math.Max(market.High, price);
        market.Low = Math.Min(market.Low, price);
        market.LastObservedAt = observation.At;

        return signal;
    }

    private static ReplayOptions Pricing(StrategySettings settings, MarketObservation observation) => new()
    {
        TradeSizeSol = settings.TradeSizeSol, FeeBasisPoints = observation.ProtocolFeeBasisPoints ?? settings.FeeBasisPoints,
        SlippageBasisPoints = settings.SlippageBasisPoints, TransactionCostSol = settings.TransactionCostSol, EntrySetupCostSol = settings.SetupCostSol
    };

    private static void ObserveDay(StrategyAccount account, decimal now)
    {
        decimal day = decimal.Floor(now / 86400m);

        if (day > account.Day || account.DayOpeningEquity == 0m)
        {
            account.Day = day;
            account.DayOpeningEquity = account.Equity;
            account.DailyHalt = false;
            account.ConsecutiveLosses = 0;
        }

        if (account.Equity <= account.DayOpeningEquity * (1m - account.Settings.DailyLossFraction) || account.ConsecutiveLosses >= 4)
        {
            account.DailyHalt = true;
        }
    }

    private static bool EntriesHalted(PortfolioState state, StrategyAccount account) => state.Halted || account.Paused || account.Flatten || account.DailyHalt;

    private static bool CanOpen(PortfolioState state, StrategyAccount account, decimal cost, decimal now, string market)
    {
        bool stale = account.Positions.Any(position => now - position.MarkedAt > account.Settings.MaximumQuoteAgeSeconds);

        bool allowed = cost > 0m && !EntriesHalted(state, account) && !stale && account.Positions.Count + account.Orders.Count < account.Settings.MaxPositions &&
                       cost <= account.Available && cost <= Math.Max(0m, account.Equity) * account.Settings.MaxPositionFraction;

        IEnumerable<StrategyAccount> managed = state.Strategies.Values.Where(candidate => candidate.Settings.Asset == account.Settings.Asset && 
                                                                                          (account.Settings.Trial ? candidate.Settings.Id == account.Settings.Id : !candidate.Settings.Trial));

        IEnumerable<StrategyAccount> strategyAccounts = managed as StrategyAccount[] ?? [.. managed];

        if (strategyAccounts.Any(candidate => candidate.Positions.Any(position => now - position.MarkedAt > candidate.Settings.MaximumQuoteAgeSeconds)))
        {
            allowed = false;
        }

        decimal equity = strategyAccounts.Sum(candidate => candidate.Equity);
        decimal committed = strategyAccounts.Sum(candidate => candidate.Positions.Sum(position => position.Cost) + candidate.Reserved);

        if (committed + cost > Math.Max(0m, equity) * 0.5m)
        {
            allowed = false;
        }

        decimal instrumentCommitted = strategyAccounts.Sum(candidate => candidate.Positions.Where(position => position.Market == market).Sum(position => position.Cost) +
                                                                        candidate.Orders.Where(order => order.Market == market).Sum(order => order.Reserved));

        if (instrumentCommitted + cost > Math.Max(0m, equity) * 0.25m)
        {
            allowed = false;
        }

        if (!allowed)
        {
            account.LastDecision = "Entry blocked by budget, exposure, freshness, or halt";
        }

        return allowed;
    }

    private static void RequestExit(PortfolioPosition position, decimal now, decimal quantity, string reason)
    {
        if (position.ExitRequestedAt is null)
        {
            position.ExitRequestedAt = now;
            position.ExitQuantity = quantity;
            position.ExitReason = reason;
        }
        else if (quantity > position.ExitQuantity)
        {
            position.ExitQuantity = quantity;
            position.ExitReason = reason;
        }
    }

    private static void Move(StrategyAccount source, StrategyAccount target, decimal amount)
    {
        source.Cash -= amount;
        source.NetContributions -= amount;
        source.DayOpeningEquity -= amount;
        target.Cash += amount;
        target.NetContributions += amount;
        target.DayOpeningEquity += amount;
    }

    public static void Record(PortfolioState state, StrategyAccount account, string kind, string market, decimal amount, decimal quantity, string reason)
    {
        long sequence = state.RecentEvents.Count == 0 ? 1 : state.RecentEvents[^1].Sequence + 1;
        state.RecentEvents.Add(new(sequence, state.Now, account.Settings.Id, kind, market, amount, quantity, reason));
        account.LastDecision = reason;
    }

    public static void AssertOwnership(PortfolioState state)
    {
        if (state.Strategies.Values.Any(account => account.Reserved < 0m || account.Reserved > Math.Max(0m, account.Cash) || account.Positions.Any(position => position.Quantity <= 0m || position.Cost < 0m) ||
                                                   account.Positions.Select(position => position.Market).Distinct().Count() != account.Positions.Count))
        {
            throw new InvalidOperationException("Portfolio ownership or reservation invariant failed.");
        }
    }

    private static MarketObservation Consume(MarketObservation observation, LedgerEntry fill, StrategySettings settings)
    {
        CurveObservation curve = observation.Curve!;
        decimal quote;
        decimal tokens;

        if (fill.Kind == "Buy")
        {
            quote = decimal.Floor(settings.TradeSizeSol * CurvePricing.LamportsPerSol / (1m + (observation.ProtocolFeeBasisPoints ?? settings.FeeBasisPoints) / 10_000m));
            tokens = -fill.Quantity;
        }
        else
        {
            quote = -decimal.Floor(curve.VirtualQuoteReserves!.Value * fill.Quantity / (curve.VirtualTokenReserves!.Value + fill.Quantity));
            tokens = fill.Quantity;
        }

        return observation with
        {
            Curve = curve with
            {
                VirtualQuoteReserves = curve.VirtualQuoteReserves + quote, RealQuoteReserves = curve.RealQuoteReserves + quote,
                VirtualTokenReserves = curve.VirtualTokenReserves + tokens, RealTokenReserves = curve.RealTokenReserves + tokens
            }
        };
    }
}