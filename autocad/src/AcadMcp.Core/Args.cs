using System.Globalization;
using System.Text.Json;

namespace AcadMcp.Core;

/// <summary>Lenient reader over a tool's JSON arguments. Numbers and bools may arrive as strings.</summary>
public sealed class Args(JsonElement root)
{
    public static Args Empty { get; } = new(JsonDocument.Parse("{}").RootElement);

    public static Args Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    public JsonElement? Raw(string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in root.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value;
        return null;
    }

    public string? Str(string name)
    {
        var v = Raw(name);
        if (v is null) return null;
        var s = v.Value.ValueKind == JsonValueKind.String ? v.Value.GetString() : v.Value.GetRawText();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public string Str(string name, string fallback) => Str(name) ?? fallback;

    public int Int(string name, int fallback, int min, int max)
    {
        var s = Str(name);
        if (s is null) return fallback;
        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ToolError($"{name} must be a whole number, got '{s}'.");
        return Math.Clamp(n, min, max);
    }

    public bool Bool(string name, bool fallback)
    {
        var s = Str(name);
        if (s is null) return fallback;
        return s.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw new ToolError($"{name} must be true or false, got '{s}'."),
        };
    }

    /// <summary>Comma-separated list (or JSON array) of strings.</summary>
    public IReadOnlyList<string> List(string name)
    {
        var v = Raw(name);
        if (v is null) return [];
        if (v.Value.ValueKind == JsonValueKind.Array)
            return v.Value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return SplitList(Str(name));
    }

    public static IReadOnlyList<string> SplitList(string? s) =>
        s is null ? [] : s.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
