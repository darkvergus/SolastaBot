namespace SolastaBot.Core.Domain;

/// <summary>
/// A fully specified order, already rounded to the instrument's filters and already approved by the
/// risk gate. The order router's only job is to transmit it, idempotently by
/// <see cref="ClientOrderId"/> so that retrying a timed-out submission cannot double-fill.
/// </summary>
public sealed record OrderIntent(
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? Price,
    bool ReduceOnly,
    string ClientOrderId);
