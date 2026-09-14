using System.Net;
using SolastaBot.Chain.Domain;
using SolastaBot.Chain.Trading;
using SolastaBot.Exchange.Chain;

namespace SolastaBot.Tests.Chain.Trading;

public sealed class PumpLaunchMarketFeedTests
{
    [Fact]
    public async Task LiveFeedUsesResponseReceiptTimeAndPreservesRawReserveUnits()
    {
        ManualTimeProvider clock = new();
        const string coin = """
                            {"mint":"test-mint","created_timestamp":99000,"quote_mint":"11111111111111111111111111111111","quote_decimals":9,"protocol":"pump","telegram":"channel","twitter":"account","complete":false,"virtual_sol_reserves":32000000000,"virtual_token_reserves":1000000000000000,"real_sol_reserves":2000000000,"real_token_reserves":700000000000000}
                            """;
        using StubPumpHandler handler = new("[" + coin + "]", clock);
        using HttpClient client = new(handler);
        using PumpLaunchMarketFeed feed = new(client, clock, 0.8m);
        LaunchCandidate launch = Assert.Single(await feed.ReadLaunchesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(102m, launch.Curve.ObservedAt);
        Assert.Equal(99m, launch.CreatedAt);
        Assert.Equal(32_000_000_000m, launch.Curve.VirtualQuoteReserves);
        Assert.Equal("https", handler.RequestedUri!.Scheme);
        Assert.Equal("frontend-api-v3.pump.fun", handler.RequestedUri.Host);
    }

    [Fact]
    public async Task CurveResponseForADifferentMintCannotFillAnOrder()
    {
        ManualTimeProvider clock = new();
        using StubPumpHandler handler = new("{\"mint\":\"different-mint\"}", clock);
        using HttpClient client = new(handler);
        using PumpLaunchMarketFeed feed = new(client, clock, 0.8m);

        await Assert.ThrowsAsync<InvalidDataException>(() => feed.ReadCurveAsync("test-mint", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingFieldsStayUnknownInsteadOfBecomingTradableZeros()
    {
        ManualTimeProvider clock = new();
        using StubPumpHandler handler = new("{\"mint\":\"test-mint\"}", clock);
        using HttpClient client = new(handler);
        using PumpLaunchMarketFeed feed = new(client, clock, 0.8m);
        CurveObservation curve = await feed.ReadCurveAsync("test-mint", TestContext.Current.CancellationToken);

        Assert.False(curve.HasReserves);
        Assert.Null(curve.Complete);
    }

    [Fact]
    public async Task ThrottledEndpointDoesNotReturnAFabricatedObservation()
    {
        ManualTimeProvider clock = new();
        using StubPumpHandler handler = new("{}", clock) { StatusCode = HttpStatusCode.TooManyRequests };
        using HttpClient client = new(handler);
        using PumpLaunchMarketFeed feed = new(client, clock, 0.8m);

        await Assert.ThrowsAsync<HttpRequestException>(() => feed.ReadCurveAsync("test-mint", TestContext.Current.CancellationToken));
    }
}
