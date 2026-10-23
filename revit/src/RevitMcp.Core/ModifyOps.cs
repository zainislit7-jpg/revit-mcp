using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMcp.Core;

/// <summary>One entry of revit_modify's ops array. Lengths: numbers in the call's units, or text such as 2'-6".</summary>
public sealed class ModifyOp
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("ids")] public List<long> Ids { get; set; } = [];
    [JsonPropertyName("param")] public string? Param { get; set; }
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    [JsonPropertyName("by")] public JsonElement[]? By { get; set; }
    [JsonPropertyName("deg")] public double? Deg { get; set; }
    [JsonPropertyName("to")] public JsonElement? To { get; set; }

    // Create ops.
    [JsonPropertyName("path")] public JsonElement[][]? Path { get; set; }
    [JsonPropertyName("closed")] public bool Closed { get; set; }
    [JsonPropertyName("at")] public JsonElement[]? At { get; set; }
    [JsonPropertyName("type")] public string? TypeName { get; set; }
    [JsonPropertyName("level")] public string? Level { get; set; }
    [JsonPropertyName("height")] public JsonElement? Height { get; set; }
    [JsonPropertyName("sill")] public JsonElement? Sill { get; set; }
    [JsonPropertyName("elevation")] public JsonElement? Elevation { get; set; }
    [JsonPropertyName("wall")] public long? Wall { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }

    public static readonly string[] Edits = ["set", "move", "copy", "rotate", "type", "delete", "pin", "unpin"];
    public static readonly string[] Creates = ["wall", "floor", "door", "window", "room", "level"];
    public static readonly string[] Known = [.. Edits, .. Creates];

    public bool IsCreate => Creates.Contains(Op);

    public const int MaxOps = 200;
    public const int MaxIds = 5000;

    /// <summary>Parses and validates up front, so a bad op fails before Revit opens a transaction.</summary>
    public static List<ModifyOp> ParseAll(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("ops is required: a JSON array of ops.");
        List<ModifyOp>? ops;
        try
        {
            var trimmed = json.TrimStart();
            ops = trimmed.StartsWith('{')
                ? [JsonSerializer.Deserialize<ModifyOp>(json, Json.Options)!]
                : JsonSerializer.Deserialize<List<ModifyOp>>(json, Json.Options);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("ops is not valid JSON: " + ex.Message);
        }
        if (ops is null || ops.Count == 0) throw new ArgumentException("ops is empty.");
        if (ops.Count > MaxOps) throw new ArgumentException($"At most {MaxOps} ops per call.");

        for (var i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            o.Op = o.Op.Trim().ToLowerInvariant();
            var where = $"ops[{i}] ({o.Op})";
            if (!Known.Contains(o.Op)) throw new ArgumentException($"{where}: unknown op. Use {string.Join(", ", Known)}.");
            if (!o.IsCreate && o.Ids.Count == 0) throw new ArgumentException($"{where}: ids is empty.");
            if (o.Ids.Count > MaxIds) throw new ArgumentException($"{where}: at most {MaxIds} ids.");
            switch (o.Op)
            {
                case "set" when string.IsNullOrWhiteSpace(o.Param) || o.Value is null:
                    throw new ArgumentException($"{where}: needs param and value.");
                case "move" or "copy" when o.By is not { Length: 2 or 3 } || !o.By.All(IsLength):
                    throw new ArgumentException($"{where}: needs by=[x,y] or [x,y,z]: numbers, or text like \"2'-6\\\"\" or \"1500 mm\".");
                case "rotate" when o.Deg is null:
                    throw new ArgumentException($"{where}: needs deg.");
                case "type" when o.To is null:
                    throw new ArgumentException($"{where}: needs to (type id or name).");
                case "wall" when o.Path is not { Length: >= 2 } || !o.Path.All(IsPoint):
                    throw new ArgumentException($"{where}: needs path=[[x,y],[x,y],...] with at least 2 points.");
                case "floor" when o.Path is not { Length: >= 3 } || !o.Path.All(IsPoint):
                    throw new ArgumentException($"{where}: needs path=[[x,y],...] with at least 3 corners.");
                case "door" or "window" or "room" when o.At is null || !IsPoint(o.At):
                    throw new ArgumentException($"{where}: needs at=[x,y].");
                case "level" when o.Elevation is not { } e || !IsLength(e):
                    throw new ArgumentException($"{where}: needs elevation.");
            }
            foreach (var (len, name) in new[] { (o.Height, "height"), (o.Sill, "sill") })
                if (len is { } l && !IsLength(l)) throw new ArgumentException($"{where}: {name} must be a number or text like \"10'\".");
        }
        return ops;
    }

    private static bool IsPoint(JsonElement[] p) => p.Length is 2 or 3 && p.All(IsLength);

    public static bool IsLength(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number
        || (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()));

    /// <summary>Units for bare numbers. "project" means the model's own length unit.</summary>
    public static readonly string[] Units = ["project", "ft", "in", "mm", "cm", "m"];

    public static string ParseUnits(string? units)
    {
        var u = (units ?? "project").Trim().ToLowerInvariant() switch
        {
            "feet" or "foot" or "'" => "ft",
            "inch" or "inches" or "\"" => "in",
            "millimeters" or "millimetres" => "mm",
            "meters" or "metres" => "m",
            var other => other,
        };
        return Units.Contains(u) ? u : throw new ArgumentException($"units must be one of {string.Join(", ", Units)}.");
    }

    /// <summary>A JSON scalar as the text Revit's SetValueString / Set expect.</summary>
    public static string? Text(JsonElement? v) => v?.ValueKind switch
    {
        JsonValueKind.String => v.Value.GetString(),
        JsonValueKind.Number => v.Value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Null => null,
        null => null,
        _ => v.Value.GetRawText(),
    };
}
