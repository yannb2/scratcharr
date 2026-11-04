using System.Globalization;
using System.Text.Json;

namespace Scratcharr.Clients;

/// <summary>Forgiving readers for the APIs' JSON: a missing or odd field gives the default, never an exception.</summary>
internal static class Json
{
    public static string Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return "";
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    public static long Long(this JsonElement e, string name, long fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        }

        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            ? s
            : fallback;
    }

    public static double Double(this JsonElement e, string name, double fallback = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.GetDouble();
        }

        return v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? s
            : fallback;
    }

    public static bool Bool(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    public static JsonElement Get(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    public static IEnumerable<JsonElement> Items(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    /// <summary>Seconds since 1970 to UTC; 0 or negative (qBittorrent's "never") gives null.</summary>
    public static DateTime? Unix(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : null;

    public static DateTime? Date(this JsonElement e, string name) =>
        DateTime.TryParse(e.Str(name), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
