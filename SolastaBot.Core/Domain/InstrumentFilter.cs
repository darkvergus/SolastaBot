using System;

namespace SolastaBot.Core.Domain;

/// <summary>
/// Forces prices and quantities onto the exchange's grid. Filter violations are the largest single
/// source of rejected orders, so every submission goes through <see cref="Prepare"/> and nothing
/// else is allowed to construct an <see cref="OrderIntent"/> quantity.
/// </summary>
/// <remarks>
/// Everything here is <see cref="decimal"/> on purpose. Tick and step sizes are exact decimal
/// fractions; doing this arithmetic in binary floating point produces quantities that look correct
/// when printed and are rejected by the exchange.
/// </remarks>
public static class InstrumentFilter
{
    /// <summary>Largest multiple of <paramref name="step"/> not exceeding <paramref name="value"/>.</summary>
    /// <remarks>
    /// Quantities always round down. Rounding up would spend margin the risk gate did not approve.
    /// </remarks>
    public static decimal FloorToStep(decimal value, decimal step)
    {
        if (step <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Step size must be positive.");
        }

        return Math.Floor(value / step) * step;
    }

    public static decimal RoundToTick(decimal price, decimal tick, PriceRounding rounding)
    {
        if (tick <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tick), tick, "Tick size must be positive.");
        }

        decimal ticks = price / tick;
        decimal rounded = rounding switch
        {
            PriceRounding.Down => Math.Floor(ticks),
            PriceRounding.Up => Math.Ceiling(ticks),
            _ => Math.Round(ticks, MidpointRounding.ToEven)
        };

        return rounded * tick;
    }

    /// <summary>
    /// Rounds an order onto the grid and checks it against the minimum quantity and notional.
    /// </summary>
    /// <param name="referencePrice">Price used for the notional check; the mark price for a market order.</param>
    public static FilterResult Prepare(Instrument instrument, decimal quantity, decimal? price, decimal referencePrice, PriceRounding rounding = PriceRounding.Nearest)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        if (quantity <= 0m)
        {
            return FilterResult.Reject("Quantity is not positive.");
        }

        if (referencePrice <= 0m)
        {
            return FilterResult.Reject("Reference price is not positive.");
        }

        decimal roundedQuantity = FloorToStep(quantity, instrument.StepSize);
        if (roundedQuantity < instrument.MinQuantity)
        {
            return FilterResult.Reject($"Quantity {roundedQuantity} is below the minimum {instrument.MinQuantity} for {instrument.Symbol}.");
        }

        decimal? roundedPrice = price is null ? null : RoundToTick(price.Value, instrument.TickSize, rounding);

        if (roundedPrice is <= 0m)
        {
            return FilterResult.Reject("Price rounded to zero or below.");
        }

        decimal notional = roundedQuantity * (roundedPrice ?? referencePrice);
        if (notional < instrument.MinNotional)
        {
            return FilterResult.Reject($"Notional {notional} is below the minimum {instrument.MinNotional} for {instrument.Symbol}.");
        }

        return new(roundedQuantity, roundedPrice, null);
    }

    /// <summary>True when the value sits exactly on the grid. Used by the filter-compliance tests.</summary>
    public static bool IsOnGrid(decimal value, decimal step) => step > 0m && value % step == 0m;
}
