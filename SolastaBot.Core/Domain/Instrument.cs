namespace SolastaBot.Core.Domain;

/// <summary>
/// The exchange's trading rules for one symbol. Every order must satisfy these filters or the
/// exchange rejects it, so this record is the input to <see cref="InstrumentFilter"/>.
/// </summary>
/// <param name="TickSize">Price increment. Prices must be an exact multiple.</param>
/// <param name="StepSize">Quantity increment. Quantities must be an exact multiple.</param>
/// <param name="MinNotional">Smallest allowed price * quantity.</param>
public sealed record Instrument(string Symbol, string BaseAsset, string QuoteAsset, decimal TickSize, decimal StepSize, decimal MinQuantity, decimal MinNotional, int MaxLeverage)
{
    /// <summary>Binance USD-M BTCUSDT as of 2026. Used by tests and as a sane default.</summary>
    public static Instrument BtcUsdtPerpetual { get; } = new(Symbol: "BTCUSDT", BaseAsset: "BTC", QuoteAsset: "USDT", TickSize: 0.10m, StepSize: 0.001m, MinQuantity: 0.001m,
        MinNotional: 100m, MaxLeverage: 125);
}
