namespace SolastaBot.Core.Risk;

public enum RiskOutcome
{
    /// <summary>Size approved exactly as derived from the risk budget.</summary>
    Approved,

    /// <summary>Approved at a smaller size than the risk budget alone would allow.</summary>
    Reduced,

    /// <summary>Strategy wants flat, or already is. Nothing to size.</summary>
    NoPosition,

    /// <summary>Trading is halted; any open position must be closed and no new one opened.</summary>
    Halted,

    /// <summary>The trade cannot be expressed within the exchange's filters or the account's equity.</summary>
    Rejected
}