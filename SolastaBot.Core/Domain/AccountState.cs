namespace SolastaBot.Core.Domain;

/// <summary>Margin account snapshot in quote currency.</summary>
public readonly record struct AccountState(decimal WalletBalance, decimal UnrealisedPnl)
{
    /// <summary>Wallet plus open-position profit. This is what position sizing risks a fraction of.</summary>
    public decimal Equity => WalletBalance + UnrealisedPnl;
}
