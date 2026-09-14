using SolastaBot.Chain.Domain;

namespace SolastaBot.Chain.Replay;

public sealed class LaunchReplay
{
    public ReplayTrade Run(LaunchObservation launch, IReadOnlyList<CurveObservation> observations, ReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (!launch.Admitted)
        {
            return Empty(launch, ReplayOutcome.NotAdmitted);
        }

        if (launch.QuoteMint != "11111111111111111111111111111111" || launch.QuoteDecimals != 9 || launch.Protocol != "pump")
        {
            return Empty(launch, ReplayOutcome.UnsupportedMarket);
        }

        decimal? previousTime = null;
        foreach (CurveObservation observation in observations)
        {
            if (observation.Mint != launch.Mint || previousTime.HasValue && observation.ObservedAt <= previousTime.Value)
            {
                throw new ArgumentException("Observations must belong to one mint and have strictly increasing timestamps.", nameof(observations));
            }

            previousTime = observation.ObservedAt;
        }

        decimal requestedEntry = launch.SeenAt + options.EntryDelaySeconds;
        int entryIndex = -1;
        for (int index = 0; index < observations.Count; index++)
        {
            if (observations[index].ObservedAt >= requestedEntry)
            {
                entryIndex = index;
                break;
            }
        }

        if (entryIndex < 0 || observations[entryIndex].ObservedAt - requestedEntry > options.MaxObservationGapSeconds)
        {
            return Empty(launch, ReplayOutcome.MissingEntry);
        }

        CurveObservation entry = observations[entryIndex];
        if (entry.Error is not null || entry.Complete is null || !entry.HasReserves)
        {
            return Empty(launch, ReplayOutcome.MissingEntry);
        }

        decimal? tokens = CurvePricing.Buy(entry, options);
        if (!tokens.HasValue)
        {
            return Empty(launch, ReplayOutcome.EntryUnavailable);
        }

        decimal entryCost = options.TradeSizeSol + options.TransactionCostSol + options.EntrySetupCostSol;
        decimal lastObservation = entry.ObservedAt;
        decimal? signalTime = null;
        ReplayOutcome reason = ReplayOutcome.TimeExit;

        for (int index = entryIndex + 1; index < observations.Count; index++)
        {
            CurveObservation observation = observations[index];
            if (observation.ObservedAt - lastObservation > options.MaxObservationGapSeconds)
            {
                return Unknown(launch, entry, entryCost, signalTime, ReplayOutcome.ObservationGap, options);
            }

            if (observation.Complete == true)
            {
                return Unknown(launch, entry, entryCost, signalTime, ReplayOutcome.Migration, options);
            }

            if (observation.Error is not null || observation.Complete is null || !observation.HasReserves)
            {
                continue;
            }

            lastObservation = observation.ObservedAt;
            decimal? proceeds = CurvePricing.Sell(observation, tokens.Value, options);

            if (signalTime.HasValue)
            {
                if (observation.ObservedAt < signalTime.Value + options.ExitDelaySeconds)
                {
                    continue;
                }

                if (!proceeds.HasValue)
                {
                    return Unknown(launch, entry, entryCost, signalTime, ReplayOutcome.InsufficientLiquidity, options);
                }

                decimal profit = proceeds.Value - entryCost;
                return new(launch.Mint, launch.Arm, launch.AdmissionProbability, reason, entry.ObservedAt, signalTime, observation.ObservedAt,
                    entryCost, proceeds.Value, profit, profit);
            }

            decimal? netReturn = proceeds / entryCost - 1m;
            if (observation.ObservedAt >= entry.ObservedAt + options.HoldSeconds)
            {
                reason = ReplayOutcome.TimeExit;
            }
            else if (!proceeds.HasValue)
            {
                reason = ReplayOutcome.StopLoss;
            }
            else if (netReturn >= options.TakeProfitFraction)
            {
                reason = ReplayOutcome.TakeProfit;
            }
            else if (netReturn <= -options.StopLossFraction)
            {
                reason = ReplayOutcome.StopLoss;
            }
            else
            {
                continue;
            }

            signalTime = observation.ObservedAt;
        }

        return Unknown(launch, entry, entryCost, signalTime, ReplayOutcome.MissingExit, options);
    }

    private static ReplayTrade Empty(LaunchObservation launch, ReplayOutcome outcome) =>
        new(launch.Mint, launch.Arm, launch.AdmissionProbability, outcome, null, null, null, 0m, null, null, null);

    private static ReplayTrade Unknown(LaunchObservation launch, CurveObservation entry, decimal cost, decimal? signalTime, ReplayOutcome outcome, ReplayOptions options) =>
        new(launch.Mint, launch.Arm, launch.AdmissionProbability, outcome, entry.ObservedAt, signalTime, null, cost, null, null, -cost - options.TransactionCostSol);
}
