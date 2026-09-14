using SolastaBot.Chain.Trading;

namespace SolastaBot.Exchange.Chain.Solana;

public sealed record ConnectedOptions
{
    public string Mode { get; init; } = "Simulate";
    public string Network { get; init; } = "Devnet";
    public Uri RpcUrl { get; init; } = new("https://api.devnet.solana.com");
    public required string WalletPath { get; init; }
    public required PaperTradingOptions Strategy { get; init; }
    public ulong FeeReserveLamports { get; init; } = 10_000_000;
    public int SlippageBasisPoints { get; init; } = 300;
    public IReadOnlyList<string> DevnetMints { get; init; } = [];
    public bool DiscoverMainnetLaunches { get; init; }

    public void Validate()
    {
        if (Mode is not ("Simulate" or "Devnet"))
        {
            throw new ArgumentException("Execution mode must be Simulate or Devnet. Mainnet sending is disabled.");
        }

        if (Network is not ("Devnet" or "Mainnet") || Mode == "Devnet" && Network != "Devnet")
        {
            throw new ArgumentException("Devnet mode requires the Devnet network.");
        }

        if (!RpcUrl.IsAbsoluteUri || RpcUrl.Scheme != "https" || RpcUrl.UserInfo.Length > 0)
        {
            throw new ArgumentException("Use an HTTPS RPC URL without embedded credentials.");
        }

        if (DiscoverMainnetLaunches && Network != "Mainnet")
        {
            throw new ArgumentException("The public launch feed describes mainnet only.");
        }

        if (DevnetMints.Count > 100 || DevnetMints.Distinct(StringComparer.Ordinal).Count() != DevnetMints.Count)
        {
            throw new ArgumentException("Use at most 100 distinct test mints.");
        }

        if (DevnetMints.Count > 0 && Network != "Devnet")
        {
            throw new ArgumentException("The explicit mint watchlist is devnet-only.");
        }

        if (string.IsNullOrWhiteSpace(WalletPath))
        {
            throw new ArgumentException("WalletPath is required.");
        }

        Strategy.Validate();
        if (FeeReserveLamports == 0 || FeeReserveLamports > 100_000_000 || SlippageBasisPoints is < 0 or > 1000)
        {
            throw new ArgumentException("Invalid fee reserve or slippage bound.");
        }

        if (Strategy.Execution.TradeSizeSol + FeeReserveLamports / 1_000_000_000m > Strategy.Capital.ChainCapitalSol * Strategy.MaxPositionFraction)
        {
            throw new ArgumentException("Trade plus the full fee reserve exceeds the position limit.");
        }
    }
}
