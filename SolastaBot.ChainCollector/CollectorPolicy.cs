namespace SolastaBot.ChainCollector;

public static class CollectorPolicy
{
    public static int? GetPollIntervalSeconds(double ageSeconds, IReadOnlyList<PollScheduleEntry> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        foreach (PollScheduleEntry scheduleEntry in schedule)
        {
            if (ageSeconds < scheduleEntry.ToSeconds)
            {
                return scheduleEntry.EverySeconds;
            }
        }

        return null;
    }

    public static decimal? CalculateThreshold(IReadOnlyCollection<decimal> baseline, int minimumSamples, decimal buyInRatio)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        if (baseline.Count < minimumSamples)
        {
            return null;
        }

        List<decimal> ordered = [.. baseline];
        ordered.Sort();

        int middle = ordered.Count / 2;

        decimal median = ordered.Count % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) / 2m : ordered[middle];

        return median * buyInRatio;
    }

    public static string? Classify(string? quoteMint, bool hasTelegram, bool hasTwitter, decimal virtualQuoteReserve, decimal? threshold, string solQuoteMint)
    {
        if (!hasTelegram || !hasTwitter || threshold is null || virtualQuoteReserve <= threshold.Value)
        {
            return null;
        }

        return string.Equals(quoteMint, solQuoteMint, StringComparison.Ordinal) ? "filtered_sol" : "filtered_alt";
    }
}