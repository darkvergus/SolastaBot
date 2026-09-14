namespace SolastaBot.Chain.Trading;

public static class PositionExitPolicy
{
    public static string? Reason(decimal costSol, decimal? proceedsSol, decimal enteredAt, decimal now, PaperTradingOptions options, TradingControl control, bool dailyHalt)
    {
        if (control.Flatten)
        {
            return "Manual flatten";
        }

        if (dailyHalt)
        {
            return "Daily loss limit";
        }

        if (now - enteredAt >= options.Execution.HoldSeconds)
        {
            return "Time exit";
        }

        if (proceedsSol is null || proceedsSol <= costSol * (1m - options.Execution.StopLossFraction))
        {
            return "Stop loss";
        }

        if (proceedsSol >= costSol * (1m + options.Execution.TakeProfitFraction))
        {
            return "Take profit";
        }

        return null;
    }
}
