using System.Globalization;
using System.Text.Json;

namespace DbdPresence.Vision;

/// <summary>Strict accessors over <see cref="JsonElement"/>: a missing or mistyped field is a <see cref="FormatException"/> that names the field.</summary>
internal static class JsonFields
{
    public static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new FormatException($"Missing property '{name}'.");

    /// <summary>Returns the items of the array property <paramref name="name"/>; any other kind of value is a <see cref="FormatException"/>, never an <see cref="InvalidOperationException"/>.</summary>
    public static JsonElement.ArrayEnumerator Items(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw new FormatException($"'{name}' must be a list.");
    }

    public static string Str(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException($"'{name}' must be a string.");
    }

    public static string? OptionalStr(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? (value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException($"'{name}' must be a string or null."))
            : null;

    public static int Int(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : throw new FormatException($"'{name}' must be an integer.");
    }

    public static double Double(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw new FormatException($"'{name}' must be a number.");
    }

    public static bool Bool(JsonElement element, string name)
    {
        var value = Prop(element, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new FormatException($"'{name}' must be true or false.");
    }

    public static DateTimeOffset Time(JsonElement element, string name) =>
        DateTimeOffset.TryParse(Str(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : throw new FormatException($"'{name}' must be an ISO 8601 timestamp.");

    public static JsonDocument Parse(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{what} is not valid JSON: {ex.Message}", ex);
        }
    }
}
