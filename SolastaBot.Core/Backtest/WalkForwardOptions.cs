namespace SolastaBot.Core.Backtest;

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