using System;

namespace SolastaBot.Core.Domain;

/// <summary>One round trip, from opening fill to closing fill. The unit of the backtest ledger.</summary>
public sealed record TradeRecord(string Symbol, PositionSide Side, DateTime OpenedAt, DateTime ClosedAt, decimal Quantity, decimal EntryPrice, decimal ExitPrice, decimal GrossPnl,
    decimal Fees, decimal Funding, ExitReason Reason)
{
    /// <summary>What actually landed in the wallet: gross less fees and less funding paid.</summary>
    public decimal NetPnl => GrossPnl - Fees - Funding;

    public bool IsWin => NetPnl > 0m;
}
