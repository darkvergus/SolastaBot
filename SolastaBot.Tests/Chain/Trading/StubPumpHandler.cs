using System.Net;
using System.Text;

namespace SolastaBot.Tests.Chain.Trading;

internal sealed class StubPumpHandler(string response, ManualTimeProvider timeProvider) : HttpMessageHandler
{
    internal HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
    internal Uri? RequestedUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Headers.Authorization);
        RequestedUri = request.RequestUri;
        timeProvider.UtcNow = timeProvider.UtcNow.AddSeconds(2);
        return Task.FromResult(new HttpResponseMessage(StatusCode) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
    }
}
