using System.Net;
using System.Text;
using System.Text.Json;

namespace SolastaBot.Tests.Chain.Execution;

internal sealed class RpcTestHandler(Func<string, JsonElement, object?> respond) : HttpMessageHandler
{
    public List<string> Methods { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        string method = document.RootElement.GetProperty("method").GetString()!;
        Methods.Add(method);
        object? result = respond(method, document.RootElement.GetProperty("params"));
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = document.RootElement.GetProperty("id").GetInt64(), result }), Encoding.UTF8, "application/json") };
    }
}
