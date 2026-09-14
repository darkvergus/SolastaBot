namespace SolastaBot.Chain.Trading;

public sealed record PaperTradingState
{
    public decimal CashSol { get; init; }
    public decimal StartedAt { get; init; }
    public decimal ObservedAt { get; init; }
    public decimal Day { get; init; }
    public decimal DayOpeningEquitySol { get; init; }
    public bool DailyHalt { get; init; }
    public long EventSequence { get; init; }
    public IReadOnlyList<PendingEntry> Pending { get; init; } = [];
    public IReadOnlyList<PaperPosition> Positions { get; init; } = [];
    public decimal EquitySol => CashSol + Positions.Sum(position => position.MarkSol);

    public static PaperTradingState Create(decimal now, PaperTradingOptions options)
    {
        options.Validate();
        return new()
        {
            CashSol = options.Capital.ChainCapitalSol,
            StartedAt = now,
            ObservedAt = now,
            Day = decimal.Floor(now / 86400m),
            DayOpeningEquitySol = options.Capital.ChainCapitalSol
        };
    }
}
