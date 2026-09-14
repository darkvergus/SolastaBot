namespace SolastaBot.Chain.Domain;

public sealed record CurveObservation(string Mint, decimal ObservedAt, bool? Complete, decimal? VirtualQuoteReserves, decimal? VirtualTokenReserves,
    decimal? RealQuoteReserves, decimal? RealTokenReserves, string? Error)
{
    public bool HasReserves => VirtualQuoteReserves > 0m && VirtualTokenReserves > 0m && RealQuoteReserves >= 0m && RealTokenReserves >= 0m;
}
