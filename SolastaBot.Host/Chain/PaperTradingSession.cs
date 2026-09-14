using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Data.Chain.Trading;

namespace SolastaBot.Host.Chain;

public sealed class PaperTradingSession(PaperStateStore store, LaunchTradingEngine engine, PaperTradingState state)
{
    public PaperTradingState State { get; private set; } = state;

    public async Task<IReadOnlyList<TradingEvent>> DiscoverAsync(IReadOnlyList<LaunchCandidate> launches, TradingControl control, CancellationToken cancellationToken)
    {
        HashSet<string> seen = await store.FindSeenAsync(launches.Select(launch => launch.Mint), cancellationToken);
        HashSet<string> discovered = new(StringComparer.Ordinal);
        List<TradingEvent> events = [];
        PaperTradingState next = State;
        foreach (LaunchCandidate launch in launches)
        {
            if (seen.Contains(launch.Mint) || !discovered.Add(launch.Mint))
            {
                continue;
            }

            PaperTransition transition = engine.Discover(next, launch, control);
            next = transition.State;
            events.AddRange(transition.Events);
        }

        await store.SaveAsync(next, events, discovered, cancellationToken);
        State = next;
        return events;
    }

    public async Task<IReadOnlyList<TradingEvent>> ObserveAsync(CurveObservation observation, TradingControl control, CancellationToken cancellationToken)
    {
        PaperTransition transition = engine.Observe(State, observation, control);
        await store.SaveAsync(transition.State, transition.Events, [], cancellationToken);
        State = transition.State;
        return transition.Events;
    }

    public async Task<IReadOnlyList<TradingEvent>> TickAsync(decimal now, TradingControl control, CancellationToken cancellationToken)
    {
        PaperTransition transition = engine.Tick(State, now, control);
        await store.SaveAsync(transition.State, transition.Events, [], cancellationToken);
        State = transition.State;
        return transition.Events;
    }
}
