using System.Text.Json;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Replay;

namespace SolastaBot.Tests.Chain;

internal static class ChainFixture
{
    internal static LaunchObservation Launch => new("test-mint", 100m, "11111111111111111111111111111111", 9, "pump", "filtered_sol", 1m, true);

    internal static ReplayOptions Options => new()
    {
        TradeSizeSol = 1.01m,
        FeeBasisPoints = 100m,
        SlippageBasisPoints = 0m,
        TransactionCostSol = 0.01m,
        EntrySetupCostSol = 0.02m,
        EntryDelaySeconds = 5m,
        ExitDelaySeconds = 1m,
        HoldSeconds = 5m,
        TakeProfitFraction = 10m,
        StopLossFraction = 0.99m
    };

    internal static CurveObservation Curve(decimal time, decimal quoteSol = 9m, decimal tokens = 1000m) => new("test-mint", time, false, 
        quoteSol * CurvePricing.LamportsPerSol, tokens, quoteSol * CurvePricing.LamportsPerSol, tokens, null);

    internal static string LaunchJson(decimal seenAt = 100m, bool admitted = true, decimal probability = 1m) => JsonSerializer.Serialize(new
    {
        mint = "test-mint",
        seen_at = seenAt,
        quote_mint = "11111111111111111111111111111111",
        quote_decimals = 9,
        protocol = "pump",
        arm = "filtered_sol",
        admit_p = probability,
        admitted
    });

    internal static string PollJson(decimal time, decimal quoteSol = 9m, decimal tokens = 1000m) => JsonSerializer.Serialize(new
    {
        mint = "test-mint",
        t = time,
        complete = false,
        virtual_sol_reserves = quoteSol * CurvePricing.LamportsPerSol,
        virtual_token_reserves = tokens,
        real_sol_reserves = quoteSol * CurvePricing.LamportsPerSol,
        real_token_reserves = tokens
    });
}
