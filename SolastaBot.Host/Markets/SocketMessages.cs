using System.Net.WebSockets;
using System.Text.Json;

namespace SolastaBot.Host.Markets;

public static class SocketMessages
{
    public static async Task<JsonDocument> ReadAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using MemoryStream message = new();
        byte[] buffer = new byte[16384];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new IOException("Market socket closed.");
            }

            if (message.Length + result.Count > 4_000_000)
            {
                throw new InvalidDataException("Market message exceeds size limit.");
            }

            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(message.ToArray());
    }

    public static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(new(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }
}
