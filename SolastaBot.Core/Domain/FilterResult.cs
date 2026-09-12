namespace SolastaBot.Core.Domain;

/// <summary>Outcome of forcing an order onto the exchange's price and quantity grid.</summary>
public readonly record struct FilterResult(decimal Quantity, decimal? Price, string? Rejection)
{
    public bool Accepted => Rejection is null;

    public static FilterResult Reject(string reason) => new(0m, null, reason);
}