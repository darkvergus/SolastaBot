namespace SolastaBot.Core.Domain;

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