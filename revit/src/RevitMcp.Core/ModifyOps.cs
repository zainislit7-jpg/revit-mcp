using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitMcp.Core;

/// <summary>One entry of revit_modify's ops array. Lengths are mm.</summary>
public sealed class ModifyOp
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";
    [JsonPropertyName("ids")] public List<long> Ids { get; set; } = [];
    [JsonPropertyName("param")] public string? Param { get; set; }
    [JsonPropertyName("value")] public JsonElement? Value { get; set; }
    [JsonPropertyName("by")] public double[]? By { get; set; }
    [JsonPropertyName("deg")] public double? Deg { get; set; }
    [JsonPropertyName("to")] public JsonElement? To { get; set; }

    public static readonly string[] Known = ["set", "move", "copy", "rotate", "type", "delete", "pin", "unpin"];

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
            if (o.Ids.Count == 0) throw new ArgumentException($"{where}: ids is empty.");
            if (o.Ids.Count > MaxIds) throw new ArgumentException($"{where}: at most {MaxIds} ids.");
            switch (o.Op)
            {
                case "set" when string.IsNullOrWhiteSpace(o.Param) || o.Value is null:
                    throw new ArgumentException($"{where}: needs param and value.");
                case "move" or "copy" when o.By is not { Length: 2 or 3 }:
                    throw new ArgumentException($"{where}: needs by=[x,y] or [x,y,z] in mm.");
                case "rotate" when o.Deg is null:
                    throw new ArgumentException($"{where}: needs deg.");
                case "type" when o.To is null:
                    throw new ArgumentException($"{where}: needs to (type id or name).");
            }
        }
        return ops;
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
