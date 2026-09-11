using SolastaBot.Core.Domain;

namespace SolastaBot.Core.Risk;

/// <summary>
/// The mutable half of risk: what has happened today and whether a human has pulled the handle.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="RiskGate"/> so the sizing arithmetic stays a pure function that can
/// be tested without winding a clock forward.
/// </remarks>
public sealed class RiskLedger
{
    private readonly RiskOptions options;
    private DateOnly day;
    private decimal dayOpeningEquity;
    private bool started;

    public RiskLedger(RiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.options = options;
    }

    public int ConsecutiveLosses { get; private set; }

    public bool KillSwitchEngaged { get; private set; }

    public decimal DayOpeningEquity => dayOpeningEquity;

    /// <summary>
    /// Advances the ledger's notion of the current UTC day, resetting both daily halts.
    /// </summary>
    /// <remarks>
    /// The consecutive-loss counter resets here as well as the loss baseline, and it must. A halt
    /// that can only be cleared by a winning trade can never be cleared at all, because the halt
    /// itself prevents the trade that would clear it. A five-year backtest caught exactly that: four
    /// losses in a row in May 2021 stopped the strategy for the remaining four and a half years and
    /// the run still reported a plausible-looking profit. Both limits therefore mean the same thing,
    /// which is a stop for the remainder of the UTC day.
    /// </remarks>
    public void Observe(DateTime utcNow, decimal equity)
    {
        DateOnly today = DateOnly.FromDateTime(utcNow);
        if (!started || today != day)
        {
            day = today;
            dayOpeningEquity = equity;
            ConsecutiveLosses = 0;
            started = true;
        }
    }

    public void RecordTrade(TradeRecord trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        ConsecutiveLosses = trade.IsWin ? 0 : ConsecutiveLosses + 1;
    }

    public void EngageKillSwitch() => KillSwitchEngaged = true;

    public void ReleaseKillSwitch() => KillSwitchEngaged = false;

    public decimal DayLossFraction(decimal equity) =>
        dayOpeningEquity <= 0m ? 0m : (dayOpeningEquity - equity) / dayOpeningEquity;

    /// <summary>Returns why trading is halted, or null when it may continue.</summary>
    public string? HaltReason(decimal equity)
    {
        if (KillSwitchEngaged)
        {
            return "Kill switch engaged.";
        }

        if (ConsecutiveLosses >= options.MaxConsecutiveLosses)
        {
            return $"{ConsecutiveLosses} consecutive losses reached the limit of {options.MaxConsecutiveLosses}.";
        }

        decimal loss = DayLossFraction(equity);
        if (loss >= options.DailyLossLimitFraction)
        {
            return $"Daily loss {loss:P2} reached the limit of {options.DailyLossLimitFraction:P2}.";
        }

        return null;
    }
}
