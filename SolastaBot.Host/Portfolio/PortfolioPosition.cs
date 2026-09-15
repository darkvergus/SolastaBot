namespace SolastaBot.Host.Portfolio;

public sealed record PortfolioPosition
{
    public required string Market { get; init; }
    public decimal Quantity { get; set; }
    public decimal Cost { get; set; }
    public decimal EntryPrice { get; set; }
    public int Direction { get; init; } = 1;
    public decimal StopPrice { get; init; }
    public decimal LiquidationPrice { get; init; }
    public decimal OpenedAt { get; init; }
    public decimal Mark { get; set; }
    public decimal MarkedAt { get; set; }
    public decimal HighestUnitExit { get; set; }
    public bool PartialTaken { get; set; }
    public decimal? ExitRequestedAt { get; set; }
    public decimal ExitQuantity { get; set; }
    public string ExitReason { get; set; } = "";
    public decimal Funding { get; set; }
    public decimal ClosedPortionPnl { get; set; }
    public decimal EntryFee { get; set; }
}
