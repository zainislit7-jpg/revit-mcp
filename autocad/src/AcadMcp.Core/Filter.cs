namespace AcadMcp.Core;

/// <summary>
/// Simple filter language shared with the Revit server:
/// <c>Layer=A-WALL*,A-DOOR; Type!=TEXT; Text~kitchen</c>.
/// Clauses are ANDed, comma-separated values are ORed, <c>*</c> and <c>?</c> are wildcards,
/// <c>~</c> means "contains". Matching is case-insensitive.
/// </summary>
public sealed class Filter
{
    public sealed record Clause(string Key, string Op, IReadOnlyList<string> Values);

    public static Filter None { get; } = new([]);

    public IReadOnlyList<Clause> Clauses { get; }

    Filter(IReadOnlyList<Clause> clauses) => Clauses = clauses;

    public static Filter Parse(string? text, IReadOnlyCollection<string>? knownKeys = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var clauses = new List<Clause>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (i, op) = FindOp(part);
            if (i <= 0) throw new ToolError($"Bad filter clause '{part}'. Use Key=Value, Key!=Value or Key~text.");
            var key = part[..i].Trim();
            if (knownKeys is not null)
            {
                var canon = knownKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolError($"Unknown filter key '{key}'. Keys: {string.Join(", ", knownKeys)}.");
                key = canon;
            }
            var values = part[(i + op.Length)..].Split(',', StringSplitOptions.TrimEntries);
            clauses.Add(new Clause(key, op, values));
        }
        return new Filter(clauses);
    }

    static (int, string) FindOp(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '!' && i + 1 < s.Length && s[i + 1] == '=') return (i, "!=");
            if (s[i] == '=') return (i, "=");
            if (s[i] == '~') return (i, "~");
        }
        return (-1, "");
    }

    public bool IsEmpty => Clauses.Count == 0;

    public bool Matches(Func<string, string?> get)
    {
        foreach (var c in Clauses)
        {
            var v = get(c.Key) ?? "";
            var hit = c.Op == "~"
                ? c.Values.Any(x => v.Contains(x, StringComparison.OrdinalIgnoreCase))
                : c.Values.Any(x => Wildcard.Match(x, v));
            if (c.Op == "!=" ? hit : !hit) return false;
        }
        return true;
    }

    /// <summary>Values of the positive "=" clause for a key, used to narrow enumeration (e.g. Space).</summary>
    public IReadOnlyList<string>? ValuesFor(string key) =>
        Clauses.FirstOrDefault(c => c.Op == "=" && string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))?.Values;

    public bool Mentions(string key) => Clauses.Any(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
}

public static class Wildcard
{
    /// <summary>Case-insensitive glob match supporting * and ?.</summary>
    public static bool Match(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            { p++; t++; }
            else if (p < pattern.Length && pattern[p] == '*')
            { star = p++; mark = t; }
            else if (star >= 0)
            { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
