using System.Text.Json;

namespace SolastaBot.ChainCollector;

public sealed record PumpRequestResult(JsonElement? Data, string? Error);