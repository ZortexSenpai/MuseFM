using System.Globalization;
using System.Text.Json;

namespace MuseFM.Services;

/// <summary>Lenient readers for JSON from third-party clients and export files.</summary>
internal static class JsonValues
{
    private const long MillisecondThreshold = 100_000_000_000; // seconds value in the year 5138

    public static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>The named property of an object, or an undefined element when it is missing or not an object.</summary>
    public static JsonElement Child(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;

    /// <summary>Reads either an array of strings or a single string.</summary>
    public static IReadOnlyList<string> GetStringList(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String)
            return string.IsNullOrWhiteSpace(value.GetString()) ? [] : [value.GetString()!];
        if (value.ValueKind != JsonValueKind.Array) return [];

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                list.Add(item.GetString()!);
        }

        return list;
    }

    public static bool TryGetLong(JsonElement element, string property, out long result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(property, out var value)
               && TryGetLong(value, out result);
    }

    public static bool TryGetLong(JsonElement value, out long result)
    {
        result = 0;
        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out result)) return true;
                if (!value.TryGetDouble(out number)) return false;
                break;
            case JsonValueKind.String:
                var text = value.GetString();
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
                break;
            default:
                return false;
        }

        if (double.IsNaN(number) || number < -9e18 || number > 9e18) return false;
        result = (long)number;
        return true;
    }

    public static int? GetSeconds(JsonElement element, string property) =>
        TryGetLong(element, property, out var value) && value is > 0 and <= int.MaxValue ? (int)value : null;

    /// <summary>Unix seconds from a number (seconds or milliseconds) or a date string.</summary>
    public static bool TryGetUnixTime(JsonElement element, string property, out long seconds)
    {
        seconds = 0;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return false;
        if (TryGetLong(value, out seconds))
        {
            if (seconds > MillisecondThreshold) seconds /= 1000;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            seconds = date.ToUnixTimeSeconds();
            return true;
        }

        return false;
    }

    public static long NormalizeUnix(long value) => value > MillisecondThreshold ? value / 1000 : value;
}
