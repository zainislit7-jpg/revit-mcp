using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMcp.Core;

/// <summary>Lenient reader over tool arguments. Models send "50" as often as 50.</summary>
public sealed class Args(JsonObject? obj)
{
    private readonly JsonObject _obj = obj ?? new JsonObject();

    public JsonObject Raw => _obj;

    public string? Str(string name)
    {
        if (!_obj.TryGetPropertyValue(name, out var node) || node is null) return null;
        var s = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public int Int(string name, int fallback)
    {
        var s = Str(name);
        return s != null && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    public double? Num(string name)
    {
        var s = Str(name);
        return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public bool Bool(string name, bool fallback)
    {
        var s = Str(name)?.ToLowerInvariant();
        return s switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => fallback,
        };
    }

    /// <summary>Comma (or JSON array) separated list, trimmed, empties dropped.</summary>
    public List<string> List(string name)
    {
        if (_obj.TryGetPropertyValue(name, out var node) && node is JsonArray arr)
            return arr.Select(n => n?.ToString().Trim() ?? "").Where(s => s.Length > 0).ToList();
        return Csv.Split(Str(name));
    }

    public List<long> Ids(string name = "ids") =>
        List(name).Select(s => long.TryParse(s, out var v) ? v : (long?)null)
                  .Where(v => v.HasValue).Select(v => v!.Value).ToList();

    /// <summary>Clamped limit and decoded offset cursor.</summary>
    public (int Limit, int Offset) Page() =>
        (Math.Clamp(Int("limit", Limits.DefaultLimit), 1, Limits.MaxLimit), Paging.Decode(Str("cursor")));
}

public static class Csv
{
    public static List<string> Split(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>RFC 4180 field quoting.</summary>
    public static string Quote(string? v)
    {
        v ??= "";
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    /// <summary>A string safe to use as a file name.</summary>
    public static string SafeFileName(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ' ' or '\\' or '/' or ':' ? '_' : c));
}

public static class Paging
{
    public static int Decode(string? cursor) =>
        int.TryParse(cursor, out var v) && v > 0 ? v : 0;

    /// <summary>Next cursor, or null when the page reached the end.</summary>
    public static string? Next(int offset, int limit, int total) =>
        offset + limit < total ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null;
}
