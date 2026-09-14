using SolastaBot.Chain.Trading;

namespace SolastaBot.Chain.Execution;

public static class ConnectedRiskPolicy
{
    public static ConnectedState Observe(ConnectedState state, decimal now, PaperTradingOptions options)
    {
        decimal day = decimal.Floor(now / 86400m);
        if (day < state.Day)
        {
            throw new InvalidOperationException("Trading clock moved backwards.");
        }

        decimal equity = state.EquitySol(options.Capital.ChainCapitalSol);
        ConnectedState next = day > state.Day ? state with { Day = day, DayOpeningEquitySol = equity, DailyHalt = false } : state;
        return !next.DailyHalt && equity <= next.DayOpeningEquitySol * (1m - options.DailyLossFraction) ? next with { DailyHalt = true } : next;
    }
}
