using SolastaBot.Core.Domain;

namespace SolastaBot.Tests.Domain;

public sealed class InstrumentFilterTests
{
    private static readonly Instrument Btc = Instrument.BtcUsdtPerpetual;

    [Theory, InlineData("1.23456", "0.001", "1.234"), InlineData("0.0009", "0.001", "0"), InlineData("10", "0.001", "10"),
     InlineData("0.3", "0.001", "0.3")]
    public void QuantitiesAlwaysRoundDownToTheStep(string value, string step, string expected)
    {
        Assert.Equal(
            decimal.Parse(expected),
            InstrumentFilter.FloorToStep(decimal.Parse(value), decimal.Parse(step)));
    }

    [Fact]
    public void PricesRoundInTheRequestedDirection()
    {
        Assert.Equal(64_000.1m, InstrumentFilter.RoundToTick(64_000.17m, 0.1m, PriceRounding.Down));
        Assert.Equal(64_000.2m, InstrumentFilter.RoundToTick(64_000.17m, 0.1m, PriceRounding.Up));
        Assert.Equal(64_000.2m, InstrumentFilter.RoundToTick(64_000.17m, 0.1m, PriceRounding.Nearest));
    }

    [Fact]
    public void OrdersBelowTheMinimumQuantityAreRejected()
    {
        FilterResult result = InstrumentFilter.Prepare(Btc, 0.0004m, null, 60_000m);
        Assert.False(result.Accepted);
        Assert.Contains("below the minimum", result.Rejection);
    }

    [Fact]
    public void OrdersBelowTheMinimumNotionalAreRejected()
    {
        // 0.001 BTC at 60,000 is 60 USDT, under the 100 USDT floor.
        FilterResult result = InstrumentFilter.Prepare(Btc, 0.001m, null, 60_000m);
        Assert.False(result.Accepted);
        Assert.Contains("Notional", result.Rejection);
    }

    [Fact]
    public void AcceptableOrdersComeBackOnTheGrid()
    {
        FilterResult result = InstrumentFilter.Prepare(Btc, 0.0123456m, 60_000.17m, 60_000m);
        Assert.True(result.Accepted);
        Assert.Equal(0.012m, result.Quantity);
        Assert.Equal(60_000.2m, result.Price);
    }

    /// <summary>
    /// Sweeps a wide range of awkward inputs and insists every accepted order sits exactly on the
    /// exchange's grid. Filter violations are the largest single cause of rejected orders, so this
    /// checks the property rather than a handful of examples.
    /// </summary>
    [Fact]
    public void EveryAcceptedOrderSatisfiesEveryFilter()
    {
        Instrument[] instruments =
        [
            Btc,
            Btc with { TickSize = 0.01m, StepSize = 0.1m, MinQuantity = 0.1m, MinNotional = 5m },
            Btc with { TickSize = 0.5m, StepSize = 1m, MinQuantity = 1m, MinNotional = 1m },
            Btc with { TickSize = 0.0001m, StepSize = 0.00001m, MinQuantity = 0.00001m, MinNotional = 1m }
        ];

        ulong state = 0xC0FFEEUL;
        int accepted = 0;

        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            state = state * 6364136223846793005UL + 1442695040888963407UL;
            Instrument instrument = instruments[(int)(state >> 60) % instruments.Length];

            decimal quantity = (state >> 40) / 1_000_000m;
            decimal price = 1m + ((state >> 20) & 0xFFFFF) / 16m;

            FilterResult result = InstrumentFilter.Prepare(instrument, quantity, price, price);

            if (!result.Accepted)
            {
                continue;
            }

            accepted++;

            Assert.True(InstrumentFilter.IsOnGrid(result.Quantity, instrument.StepSize), $"Quantity {result.Quantity} is off the {instrument.StepSize} step grid.");

            Assert.True(InstrumentFilter.IsOnGrid(result.Price!.Value, instrument.TickSize), $"Price {result.Price} is off the {instrument.TickSize} tick grid.");

            Assert.True(result.Quantity >= instrument.MinQuantity);
            Assert.True(result.Quantity * result.Price.Value >= instrument.MinNotional);
            Assert.True(result.Quantity <= quantity, "Rounding must never increase the quantity.");
        }

        Assert.True(accepted > 1_000, $"Only {accepted} orders were accepted; the sweep is not exercising much.");
    }
}