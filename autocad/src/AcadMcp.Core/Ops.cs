using System.Globalization;
using System.Text.Json;

namespace AcadMcp.Core;

public readonly record struct Pt(double X, double Y, double Z = 0)
{
    public override string ToString() => Fmt.Pt(X, Y, Z);
}

/// <summary>One parsed, validated entry of the modify tool's <c>ops</c> array.</summary>
public sealed class Op
{
    public int Index { get; }
    public string Kind { get; }
    readonly Dictionary<string, JsonElement> _v;

    internal Op(int index, string kind, Dictionary<string, JsonElement> values)
    {
        Index = index;
        Kind = kind;
        _v = values;
    }

    public string Label => $"op {Index + 1} ({Kind})";

    public bool Has(string key) => _v.ContainsKey(key);

    public string Str(string key) => OptStr(key) ?? throw Err($"needs '{key}'");

    public string? OptStr(string key) =>
        _v.TryGetValue(key, out var e) ? (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()) : null;

    public double Num(string key) => OptNum(key) ?? throw Err($"needs number '{key}'");

    public double? OptNum(string key)
    {
        if (!_v.TryGetValue(key, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw Err($"'{key}' must be a number");
    }

    public bool? OptBool(string key)
    {
        if (!_v.TryGetValue(key, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(e.GetString(), out var b) => b,
            _ => throw Err($"'{key}' must be true or false"),
        };
    }

    public Pt P(string key) => OptP(key) ?? throw Err($"needs point '{key}' like [x,y]");

    public Pt? OptP(string key) => _v.TryGetValue(key, out var e) ? ToPt(e, key) : null;

    public IReadOnlyList<Pt> Pts(string key)
    {
        if (!_v.TryGetValue(key, out var e) || e.ValueKind != JsonValueKind.Array) throw Err($"needs '{key}' like [[x,y],[x,y]]");
        var pts = e.EnumerateArray().Select(p => ToPt(p, key)).ToList();
        if (pts.Count < 2) throw Err($"'{key}' needs at least 2 points");
        return pts;
    }

    public IReadOnlyList<string> Ids(string key = "ids")
    {
        if (!_v.TryGetValue(key, out var e)) throw Err($"needs '{key}' (handles)");
        var ids = e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToList()
            : Args.SplitList(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()).ToList();
        if (ids.Count == 0) throw Err($"'{key}' is empty");
        return ids;
    }

    public IReadOnlyDictionary<string, string>? Map(string key)
    {
        if (!_v.TryGetValue(key, out var e)) return null;
        if (e.ValueKind != JsonValueKind.Object) throw Err($"'{key}' must be an object like {{\"TAG\":\"value\"}}");
        return e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText(),
            StringComparer.OrdinalIgnoreCase);
    }

    Pt ToPt(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Array)
        {
            var n = e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToList();
            if (n.Count is 2 or 3 && n.All(double.IsFinite)) return new Pt(n[0], n[1], n.Count == 3 ? n[2] : 0);
        }
        else if (e.ValueKind == JsonValueKind.String)
        {
            var parts = e.GetString()!.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length is 2 or 3 && parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                var d = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToList();
                return new Pt(d[0], d[1], d.Count == 3 ? d[2] : 0);
            }
        }
        throw Err($"'{key}' must be a point like [x,y] or [x,y,z]");
    }

    public ToolError Err(string what) => new($"{Label}: {what}.");
}

/// <summary>Parses and validates the modify tool's <c>ops</c> JSON before anything touches the drawing.</summary>
public static class Ops
{
    // key:type, '?' = optional. Types: pt, pts, num, str, bool, ids, map.
    static readonly string[] Common = ["layer:str?", "color:str?", "linetype:str?"];

    public static readonly IReadOnlyDictionary<string, string[]> Spec = new Dictionary<string, string[]>
    {
        ["line"] = ["from:pt", "to:pt", .. Common],
        ["pline"] = ["pts:pts", "closed:bool?", "width:num?", .. Common],
        ["circle"] = ["center:pt", "r:num", .. Common],
        ["arc"] = ["center:pt", "r:num", "start:num", "end:num", .. Common],
        ["rect"] = ["p1:pt", "p2:pt", .. Common],
        ["text"] = ["at:pt", "text:str", "h:num?", "rot:num?", "style:str?", .. Common],
        ["mtext"] = ["at:pt", "text:str", "h:num?", "w:num?", "style:str?", .. Common],
        ["insert"] = ["block:str", "at:pt", "scale:num?", "rot:num?", "attrs:map?", .. Common],
        ["dim"] = ["from:pt", "to:pt", "at:pt", "rot:num?", "style:str?", .. Common],
        ["hatch"] = ["ids:ids?", "pts:pts?", "pattern:str?", "scale:num?", .. Common],
        ["layer"] = ["name:str", "color:str?", "linetype:str?", "on:bool?", "frozen:bool?", "locked:bool?", "plot:bool?", "current:bool?"],
        ["set"] = ["ids:ids", "text:str?", "attrs:map?", .. Common],
        ["move"] = ["ids:ids", "by:pt"],
        ["copy"] = ["ids:ids", "by:pt"],
        ["rotate"] = ["ids:ids", "base:pt", "deg:num"],
        ["scale"] = ["ids:ids", "base:pt", "f:num"],
        ["mirror"] = ["ids:ids", "p1:pt", "p2:pt", "keep:bool?"],
        ["erase"] = ["ids:ids"],
    };

    public static IReadOnlyList<Op> Parse(string? json, int maxOps = 500)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ToolError("ops is required: a JSON array like [{\"op\":\"line\",\"from\":[0,0],\"to\":[100,0]}].");
        JsonElement root;
        try { root = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true }).RootElement.Clone(); }
        catch (JsonException e) { throw new ToolError($"ops is not valid JSON: {e.Message}"); }
        if (root.ValueKind == JsonValueKind.Object) root = JsonDocument.Parse($"[{root.GetRawText()}]").RootElement.Clone();
        if (root.ValueKind != JsonValueKind.Array) throw new ToolError("ops must be a JSON array of objects.");
        var count = root.GetArrayLength();
        if (count == 0) throw new ToolError("ops is empty.");
        if (count > maxOps) throw new ToolError($"Too many ops ({count}); max {maxOps} per call.");

        var result = new List<Op>(count);
        var i = 0;
        foreach (var e in root.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) throw new ToolError($"op {i + 1} must be an object.");
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            string? kind = null;
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, "op", StringComparison.OrdinalIgnoreCase)) kind = p.Value.GetString()?.Trim().ToLowerInvariant();
                else if (p.Value.ValueKind != JsonValueKind.Null) values[p.Name] = p.Value;
            }
            if (kind is null || !Spec.TryGetValue(kind, out var spec))
                throw new ToolError($"op {i + 1}: unknown op '{kind}'. Ops: {string.Join(", ", Spec.Keys)}.");
            var op = new Op(i, kind, values);
            Validate(op, spec, values.Keys);
            result.Add(op);
            i++;
        }
        return result;
    }

    static void Validate(Op op, string[] spec, IEnumerable<string> keys)
    {
        var allowed = spec.Select(s => s.Split(':')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
            if (!allowed.Contains(k)) throw op.Err($"unknown key '{k}'. Allowed: {string.Join(", ", allowed)}");

        foreach (var s in spec)
        {
            var parts = s.Split(':');
            var (key, type) = (parts[0], parts[1].TrimEnd('?'));
            var optional = parts[1].EndsWith('?');
            if (optional && !op.Has(key)) continue;
            _ = type switch
            {
                "pt" => op.P(key),
                "pts" => op.Pts(key),
                "num" => op.Num(key),
                "str" => op.Str(key),
                "bool" => op.OptBool(key) ?? false,
                "ids" => op.Ids(key),
                "map" => op.Map(key),
                _ => (object?)null,
            };
        }

        if (op.Kind == "hatch" && !op.Has("ids") && !op.Has("pts")) throw op.Err("needs 'ids' (closed boundaries) or 'pts'");
        if (op.Kind is "circle" or "arc" && op.Num("r") <= 0) throw op.Err("'r' must be > 0");
        if (op.Kind == "scale" && op.Num("f") <= 0) throw op.Err("'f' must be > 0");
        if (op.Has("color")) ColorSpec.Parse(op.Str("color"));
    }
}

/// <summary>Color text: ByLayer, ByBlock, an index 1-255, or r,g,b.</summary>
public readonly record struct ColorSpec(string Kind, short Index = 0, byte R = 0, byte G = 0, byte B = 0)
{
    public static ColorSpec Parse(string s)
    {
        var t = s.Trim();
        if (t.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return new("bylayer");
        if (t.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return new("byblock");
        if (short.TryParse(t, out var i) && i is >= 1 and <= 255) return new("index", i);
        var named = t.ToLowerInvariant() switch
        {
            "red" => 1, "yellow" => 2, "green" => 3, "cyan" => 4, "blue" => 5, "magenta" => 6, "white" => 7, "grey" or "gray" => 8,
            _ => 0,
        };
        if (named > 0) return new("index", (short)named);
        var rgb = t.Split(',', StringSplitOptions.TrimEntries);
        if (rgb.Length == 3 && rgb.All(p => byte.TryParse(p, out _)))
            return new("rgb", 0, byte.Parse(rgb[0]), byte.Parse(rgb[1]), byte.Parse(rgb[2]));
        throw new ToolError($"Bad color '{s}'. Use ByLayer, ByBlock, 1-255, a name like red, or r,g,b.");
    }
}
