namespace SolastaBot.Core.Domain;

/// <summary>Which way a position faces. Quantities are always stored positive; this carries the sign.</summary>
public enum PositionSide
{
    Flat,
    Long,
    Short
}

public enum OrderSide
{
    Buy,
    Sell
}

public enum OrderType
{
    Market,
    Limit
}

/// <summary>Why a position was closed. Drives the trade ledger and the consecutive-loss counter.</summary>
public enum ExitReason
{
    None,
    Signal,
    StopLoss,
    Liquidation,
    RiskHalt,
    EndOfData
}
