using SolastaBot.Chain.Replay;

namespace SolastaBot.Chain.Trading;

public static class LaunchEntryPolicy
{
    public static string? Rejection(LaunchCandidate launch, decimal now, PaperTradingOptions options)
    {
        if (launch.QuoteMint != "11111111111111111111111111111111" || launch.QuoteDecimals != 9 || launch.Protocol != "pump")
        {
            return "Unsupported market";
        }

        if (launch.CreatedAt <= 0m || launch.CreatedAt > now || now - launch.CreatedAt > options.MaxLaunchAgeSeconds)
        {
            return "Launch age outside configured window";
        }

        if (!launch.HasTelegram || !launch.HasTwitter)
        {
            return "Launch filter not met";
        }

        if (launch.Curve.Error is not null || !launch.Curve.HasReserves || launch.Curve.Complete != false)
        {
            return "Curve unavailable";
        }

        if (launch.Curve.VirtualQuoteReserves <= options.MinimumVirtualSol * CurvePricing.LamportsPerSol || launch.Curve.RealQuoteReserves < options.MinimumRealSol * CurvePricing.LamportsPerSol)
        {
            return "Reserve threshold not met";
        }

        return null;
    }
}
