using System.Globalization;
using System.Text.Json;

namespace SolastaBot.ChainCollector;

internal static class JsonElementReader
{
    public static string? GetString(JsonElement source, string propertyName)
    {
        if (!TryGet(source, propertyName, out JsonElement value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    public static decimal GetDecimal(JsonElement source, string propertyName)
    {
        if (!TryGet(source, propertyName, out JsonElement value))
        {
            return 0m;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsedValue))
        {
            return parsedValue;
        }

        return 0m;
    }

    public static bool IsTruthy(JsonElement source, string propertyName)
    {
        if (!TryGet(source, propertyName, out JsonElement value))
        {
            return false;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.False:
                return false;

            case JsonValueKind.True:
                return true;

            case JsonValueKind.String:
                return !string.IsNullOrEmpty(value.GetString());

            case JsonValueKind.Number:
                return GetNumericTruth(value);

            case JsonValueKind.Array:
                return value.GetArrayLength() > 0;

            case JsonValueKind.Object:
                JsonElement.ObjectEnumerator objectEnumerator = value.EnumerateObject();

                return objectEnumerator.MoveNext();

            default:
                return false;
        }
    }

    public static object? GetValue(JsonElement source, string propertyName)
    {
        return TryGet(source, propertyName, out JsonElement value) ? value.Clone() : null;
    }

    private static bool TryGet(JsonElement source, string propertyName, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(propertyName, out JsonElement propertyValue))
        {
            value = propertyValue;
            return true;
        }

        value = default;
        return false;
    }

    private static bool GetNumericTruth(JsonElement value)
    {
        if (value.TryGetDecimal(out decimal decimalValue))
        {
            return decimalValue != 0m;
        }

        return value.TryGetDouble(out double doubleValue) && doubleValue != 0d;
    }
}