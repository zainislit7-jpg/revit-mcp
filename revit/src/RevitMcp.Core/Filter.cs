using System.Globalization;

namespace RevitMcp.Core;

/// <summary>
/// Tiny filter language over rows: "Level=Level 1; Name~Door; Width>900; Mark!=".
/// Operators: = != ~ (contains) !~ &gt; &lt; &gt;= &lt;=. Clauses are AND-ed. Text compares ignore case.
/// </summary>
public sealed class Filter
{
    public sealed record Clause(string Field, string Op, string Value);

    private static readonly string[] Ops = ["!=", ">=", "<=", "!~", "=", "~", ">", "<"];

    public IReadOnlyList<Clause> Clauses { get; }

    private Filter(List<Clause> clauses) => Clauses = clauses;

    public bool IsEmpty => Clauses.Count == 0;

    /// <summary>Field names the filter reads, so callers can load only those.</summary>
    public IEnumerable<string> Fields => Clauses.Select(c => c.Field);

    public static Filter Parse(string? text)
    {
        var clauses = new List<Clause>();
        if (string.IsNullOrWhiteSpace(text)) return new Filter(clauses);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (idx, op) = FindOp(part);
            if (idx <= 0) throw new FormatException($"Bad filter clause '{part}'. Use Field=Value; Field~text; Field>number.");
            clauses.Add(new Clause(part[..idx].Trim(), op, part[(idx + op.Length)..].Trim()));
        }
        return new Filter(clauses);
    }

    private static (int, string) FindOp(string s)
    {
        // Earliest operator wins; at equal positions the longer one ("!=" before "=").
        var best = (-1, "");
        foreach (var op in Ops)
        {
            var i = s.IndexOf(op, StringComparison.Ordinal);
            if (i >= 0 && (best.Item1 < 0 || i < best.Item1)) best = (i, op);
        }
        return best;
    }

    public bool Match(Row row) => Clauses.All(c => Match(c, row.GetValueOrDefault(c.Field)));

    private static bool Match(Clause c, string? actual)
    {
        actual ??= "";
        switch (c.Op)
        {
            case "=": return Eq(actual, c.Value);
            case "!=": return !Eq(actual, c.Value);
            case "~": return actual.Contains(c.Value, StringComparison.OrdinalIgnoreCase);
            case "!~": return !actual.Contains(c.Value, StringComparison.OrdinalIgnoreCase);
        }
        if (!TryNum(actual, out var a) || !TryNum(c.Value, out var b)) return false;
        return c.Op switch
        {
            ">" => a > b,
            "<" => a < b,
            ">=" => a >= b,
            "<=" => a <= b,
            _ => false,
        };
    }

    private static bool Eq(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || (TryNum(a, out var x) && TryNum(b, out var y) && Math.Abs(x - y) < 1e-6);

    /// <summary>Reads the leading number of a display value such as "2400 mm" or "12' 6\"" (feet part).</summary>
    public static bool TryNum(string s, out double v)
    {
        s = s.Trim();
        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or '-' or '+' || (s[end] is 'e' or 'E' && end > 0)))
            end++;
        return double.TryParse(s[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
