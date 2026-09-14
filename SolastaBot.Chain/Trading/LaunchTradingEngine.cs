using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Trading;

public sealed class LaunchTradingEngine(PaperTradingOptions options)
{
    private readonly PaperTradingOptions options = Validate(options);
    private readonly PaperOrderRouter router = new();

    public PaperTransition Tick(PaperTradingState state, decimal now, TradingControl control)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(now, state.ObservedAt);
        List<TradingEvent> events = [];
        decimal day = decimal.Floor(now / 86400m);
        PaperTradingState next = state with { ObservedAt = now };
        if (day > state.Day)
        {
            next = next with { Day = day, DayOpeningEquitySol = state.EquitySol, DailyHalt = false };
        }

        if (!next.DailyHalt && next.EquitySol <= next.DayOpeningEquitySol * (1m - options.DailyLossFraction))
        {
            next = next with { DailyHalt = true };
            events.Add(Event(next, events, now, "Halt", string.Empty, "Daily loss limit until next UTC day"));
        }

        List<PendingEntry> pending = [];
        foreach (PendingEntry entry in next.Pending)
        {
            if (control.HaltEntries || control.Flatten || next.DailyHalt || now > entry.ExpiresAt)
            {
                events.Add(Event(next, events, now, "CancelEntry", entry.Mint, now > entry.ExpiresAt ? "Entry expired" : "Entries halted"));
            }
            else
            {
                pending.Add(entry);
            }
        }

        List<PaperPosition> positions = [];
        foreach (PaperPosition position in next.Positions)
        {
            if (position.ExitRequestedAt is null && (control.Flatten || next.DailyHalt || now - position.EnteredAt >= options.Execution.HoldSeconds))
            {
                string reason = control.Flatten ? "Manual flatten" : next.DailyHalt ? "Daily loss limit" : "Time exit";
                positions.Add(position with { ExitRequestedAt = now, ExitReason = reason });
                events.Add(Event(next, events, now, "ExitSignal", position.Mint, reason));
            }
            else
            {
                positions.Add(position);
            }
        }

        return Finish(next with { Pending = pending, Positions = positions }, events);
    }

    public PaperTransition Discover(PaperTradingState state, LaunchCandidate launch, TradingControl control)
    {
        decimal now = launch.Curve.ObservedAt;
        PaperTransition tick = Tick(state, now, control);
        PaperTradingState next = tick.State;
        List<TradingEvent> events = [];
        string? rejection = LaunchEntryPolicy.Rejection(launch, now, options) ?? EntryRejection(next, now, control);
        if (next.Pending.Any(entry => entry.Mint == launch.Mint) || next.Positions.Any(position => position.Mint == launch.Mint))
        {
            rejection = "Mint already has an order or position";
        }

        if (rejection is not null)
        {
            events.Add(Event(next, events, now, "RejectEntry", launch.Mint, rejection, observation: launch.Curve));
        }
        else
        {
            PendingEntry entry = new(launch.Mint, now, now + options.Execution.EntryDelaySeconds,
                now + options.Execution.EntryDelaySeconds + options.Execution.MaxObservationGapSeconds);
            next = next with { Pending = [.. next.Pending, entry] };
            events.Add(Event(next, events, now, "EntrySignal", launch.Mint, "Launch filter met", options.EntryCostSol, observation: launch.Curve));
        }

        PaperTransition decision = Finish(next, events);
        return new(decision.State, [.. tick.Events, .. decision.Events]);
    }

    public PaperTransition Observe(PaperTradingState state, CurveObservation observation, TradingControl control)
    {
        PaperTransition tick = Tick(state, observation.ObservedAt, control);
        PaperTradingState next = tick.State;
        List<TradingEvent> events = [];
        PendingEntry? entry = next.Pending.FirstOrDefault(pending => pending.Mint == observation.Mint);
        PaperPosition? position = next.Positions.FirstOrDefault(open => open.Mint == observation.Mint);

        if (entry is not null && observation.ObservedAt > entry.RequestedAt && observation.ObservedAt >= entry.ExecuteAfter)
        {
            PaperTradingState withoutEntry = next with { Pending = [.. next.Pending.Where(pending => pending.Mint != entry.Mint)] };
            string? rejection = EntryRejection(withoutEntry, observation.ObservedAt, control);
            decimal? tokens = rejection is null ? router.Buy(observation, options) : null;
            next = withoutEntry;
            if (tokens.HasValue)
            {
                decimal mark = router.Sell(observation, tokens.Value, options) ?? 0m;
                PaperPosition opened = new(entry.Mint, tokens.Value, observation.ObservedAt, options.EntryCostSol, observation.ObservedAt, mark);
                next = next with { CashSol = next.CashSol - options.EntryCostSol, Positions = [.. next.Positions, opened] };
                events.Add(Event(next, events, observation.ObservedAt, "PaperBuy", entry.Mint, "Entry filled", options.EntryCostSol, tokens, observation));
            }
            else
            {
                events.Add(Event(next, events, observation.ObservedAt, "RejectBuy", entry.Mint, rejection ?? "Curve cannot fill entry", observation: observation));
            }
        }
        else if (position is not null && observation.ObservedAt > position.LastObservationAt)
        {
            if (observation.Complete == true)
            {
                PaperPosition migrated = position with { Migrated = true, MarkSol = 0m, LastObservationAt = observation.ObservedAt };
                next = Replace(next, migrated);
                events.Add(Event(next, events, observation.ObservedAt, "UnresolvedPosition", position.Mint, "Migration requires pool execution data", observation: observation));
            }
            else if (observation.Error is not null || !observation.HasReserves || observation.Complete is null)
            {
                events.Add(Event(next, events, observation.ObservedAt, "ObservationError", position.Mint, observation.Error ?? "Incomplete curve state"));
            }
            else if (!position.Migrated)
            {
                decimal? proceeds = router.Sell(observation, position.Tokens, options);
                PaperPosition marked = position with { MarkSol = proceeds ?? 0m, LastObservationAt = observation.ObservedAt };
                if (position.ExitRequestedAt.HasValue && observation.ObservedAt > position.ExitRequestedAt.Value &&
                    observation.ObservedAt >= position.ExitRequestedAt.Value + options.Execution.ExitDelaySeconds)
                {
                    if (proceeds.HasValue)
                    {
                        next = next with { CashSol = next.CashSol + proceeds.Value, Positions = [.. next.Positions.Where(open => open.Mint != position.Mint)] };
                        events.Add(Event(next, events, observation.ObservedAt, "PaperSell", position.Mint, position.ExitReason!, proceeds, position.Tokens, observation));
                    }
                    else
                    {
                        next = Replace(next, marked);
                        events.Add(Event(next, events, observation.ObservedAt, "ExitUnfilled", position.Mint, "Insufficient real reserves; position retained", observation: observation));
                    }
                }
                else
                {
                    decimal? netReturn = proceeds / position.CostSol - 1m;
                    if (position.ExitRequestedAt is null && (!proceeds.HasValue || netReturn <= -options.Execution.StopLossFraction || netReturn >= options.Execution.TakeProfitFraction))
                    {
                        string reason = netReturn >= options.Execution.TakeProfitFraction ? "Take profit" : "Stop loss";
                        marked = marked with { ExitRequestedAt = observation.ObservedAt, ExitReason = reason };
                        events.Add(Event(next, events, observation.ObservedAt, "ExitSignal", position.Mint, reason, observation: observation));
                    }

                    next = Replace(next, marked);
                }
            }
        }

        PaperTransition applied = Finish(next, events);
        PaperTransition risk = Tick(applied.State, observation.ObservedAt, control);
        return new(risk.State, [.. tick.Events, .. applied.Events, .. risk.Events]);
    }

    private string? EntryRejection(PaperTradingState state, decimal now, TradingControl control)
    {
        if (control.HaltEntries || control.Flatten || state.DailyHalt)
        {
            return "Entries halted";
        }

        if (state.Positions.Any(position => position.Migrated || now - position.LastObservationAt > options.Execution.MaxObservationGapSeconds))
        {
            return "Existing position has unresolved or stale market data";
        }

        if (state.Positions.Count + state.Pending.Count >= options.MaxPositions)
        {
            return "Position limit";
        }

        decimal available = state.CashSol - state.Pending.Count * options.EntryCostSol;
        if (options.EntryCostSol > available || options.EntryCostSol > Math.Max(0m, state.EquitySol) * options.MaxPositionFraction)
        {
            return "Insufficient chain allocation or position risk allowance";
        }

        return null;
    }

    private static PaperTradingOptions Validate(PaperTradingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }

    private static PaperTradingState Replace(PaperTradingState state, PaperPosition updated) => state with { Positions = [.. state.Positions.Select(position => 
        position.Mint == updated.Mint ? updated : position)] };

    private static TradingEvent Event(PaperTradingState state, List<TradingEvent> events, decimal now, string kind, string mint, string reason, decimal? amount = null, 
        decimal? tokens = null, CurveObservation? observation = null) => new(state.EventSequence + events.Count + 1, now, kind, mint, reason, amount, tokens, observation);

    private static PaperTransition Finish(PaperTradingState state, List<TradingEvent> events) => new(state with { EventSequence = state.EventSequence + events.Count }, events);
}
