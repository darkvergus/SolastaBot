namespace SolastaBot.Host.Portfolio;

public sealed class PerpetualExposure
{
    public required string Market { get; init; }
    public decimal Quantity { get; init; }
    public int Direction { get; init; }
    public decimal OpenedAt { get; init; }
    public decimal? ClosedAt { get; set; }
}
