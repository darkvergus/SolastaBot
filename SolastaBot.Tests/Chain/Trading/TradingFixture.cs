using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;

namespace SolastaBot.Tests.Chain.Trading;

internal static class TradingFixture
{
    internal static PaperTradingOptions Options => new()
    {
        Capital = new() { TotalPaperCapitalSol = 100m, ChainFraction = 0.1m, PerpFraction = 0.5m },
        Execution = ChainFixture.Options,
        MaxPositionFraction = 1m
    };

    internal static CurveObservation Curve(decimal time, string mint = "test-mint", decimal quoteSol = 32m, decimal tokens = 1000m) =>
        ChainFixture.Curve(time, quoteSol, tokens) with { Mint = mint };

    internal static LaunchCandidate Launch(decimal time = 100m, string mint = "test-mint") =>
        new(mint, time - 1m, "11111111111111111111111111111111", 9, "pump", true, true, Curve(time, mint));

    internal static PaperTradingState Open(LaunchTradingEngine engine, PaperTradingOptions options)
    {
        PaperTransition pending = engine.Discover(PaperTradingState.Create(100m, options), Launch(), new());
        return engine.Observe(pending.State, Curve(105m), new()).State;
    }
}
