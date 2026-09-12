namespace SolastaBot.Data.Market;

/// <summary>A bar size, paired with the exchange's code for it.</summary>
public sealed record CandleInterval(string Code, TimeSpan Duration)
{
    public static CandleInterval OneMinute { get; } = new("1m", TimeSpan.FromMinutes(1));

    public static CandleInterval ThreeMinutes { get; } = new("3m", TimeSpan.FromMinutes(3));

    public static CandleInterval FiveMinutes { get; } = new("5m", TimeSpan.FromMinutes(5));

    public static CandleInterval FifteenMinutes { get; } = new("15m", TimeSpan.FromMinutes(15));

    public static CandleInterval ThirtyMinutes { get; } = new("30m", TimeSpan.FromMinutes(30));

    public static CandleInterval OneHour { get; } = new("1h", TimeSpan.FromHours(1));

    public static CandleInterval TwoHours { get; } = new("2h", TimeSpan.FromHours(2));

    public static CandleInterval FourHours { get; } = new("4h", TimeSpan.FromHours(4));

    public static CandleInterval SixHours { get; } = new("6h", TimeSpan.FromHours(6));

    public static CandleInterval EightHours { get; } = new("8h", TimeSpan.FromHours(8));

    public static CandleInterval TwelveHours { get; } = new("12h", TimeSpan.FromHours(12));

    public static CandleInterval OneDay { get; } = new("1d", TimeSpan.FromDays(1));

    public static IReadOnlyList<CandleInterval> All { get; } =
    [
        OneMinute, ThreeMinutes, FiveMinutes, FifteenMinutes, ThirtyMinutes,
        OneHour, TwoHours, FourHours, SixHours, EightHours, TwelveHours, OneDay
    ];

    public static CandleInterval Parse(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        foreach (CandleInterval interval in All)
        {
            if (string.Equals(interval.Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return interval;
            }
        }

        throw new ArgumentException($"Unknown interval '{code}'. Known intervals: {string.Join(", ", All.Select(entry => entry.Code))}.", nameof(code));
    }

    public override string ToString() => Code;
}
