using SolastaBot.Chain.Replay;

namespace SolastaBot.Cli.Chain;

internal sealed record ChainReplayScenario(ReplayOptions Options, IReadOnlyList<ReplaySummary> Arms, IReadOnlyList<ReplayTrade> Trades);
