using System.Globalization;
using System.Text;

namespace RevitMcp.Core;

/// <summary>A row of display strings keyed by column name (case-insensitive).</summary>
public sealed class Row : Dictionary<string, string?>
{
    public Row() : base(StringComparer.OrdinalIgnoreCase) { }
}

/// <summary>Token-lean text results: a summary line plus a pipe table.</summary>
public static class Table
{
    /// <summary>
    /// Columns to show: the requested ones if given, else defaults, else every column that
    /// has a value in at least one row (so null-only columns never cost tokens).
    /// </summary>
    public static List<string> Columns(IReadOnlyList<Row> rows, IReadOnlyList<string>? requested, IReadOnlyList<string>? defaults = null)
    {
        if (requested is { Count: > 0 }) return requested.ToList();
        if (defaults is { Count: > 0 }) return defaults.ToList();
        var cols = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
            foreach (var (k, v) in row)
                if (!string.IsNullOrEmpty(v) && seen.Add(k)) cols.Add(k);
        return cols;
    }

    public static string Render(IReadOnlyList<Row> rows, IReadOnlyList<string> columns)
    {
        var sb = new StringBuilder();
        sb.AppendJoin('|', columns).Append('\n');
        foreach (var row in rows)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(Cell(row.GetValueOrDefault(columns[i])));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>One line per key for a single element: "Key: value".</summary>
    public static string RenderRecord(Row row, IReadOnlyList<string>? columns = null)
    {
        var sb = new StringBuilder();
        foreach (var key in columns ?? row.Keys.ToList())
        {
            var v = row.GetValueOrDefault(key);
            if (!string.IsNullOrEmpty(v)) sb.Append(key).Append(": ").Append(Cell(v)).Append('\n');
        }
        return sb.ToString();
    }

    public static string Cell(string? v) =>
        string.IsNullOrEmpty(v) ? "" : v.Replace('|', '/').Replace("\r", "").Replace('\n', ' ').Trim();

    /// <summary>Round to at most 3 decimals, drop trailing zeros.</summary>
    public static string Num(double v) =>
        Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Assembles a paged result and enforces the character budget.</summary>
public static class TextResult
{
    /// <summary>Slices a full list into one page.</summary>
    public static string Page(string title, IReadOnlyList<Row> all, int offset, int limit,
        IReadOnlyList<string>? fields, IReadOnlyList<string>? defaults = null, int maxChars = Limits.MaxChars) =>
        Page(title, all.Skip(offset).Take(limit).ToList(), offset, limit, all.Count, fields, defaults, maxChars);

    /// <summary>Renders an already-sliced page: "Title: total (showing a-b) next:n" then the table.</summary>
    public static string Page(string title, IReadOnlyList<Row> page, int offset, int limit, int total,
        IReadOnlyList<string>? fields, IReadOnlyList<string>? defaults = null, int maxChars = Limits.MaxChars)
    {
        var next = Paging.Next(offset, limit, total);
        var head = total == 0
            ? $"{title}: 0"
            : $"{title}: {total} (showing {offset + 1}-{offset + page.Count})" + (next != null ? $" next:{next}" : "");
        if (page.Count == 0) return head + "\n";
        return Cap(head + "\n" + Table.Render(page, Table.Columns(page, fields, defaults)), maxChars);
    }

    /// <summary>Cuts at a line boundary and tells the model how to get the rest.</summary>
    public static string Cap(string text, int maxChars = Limits.MaxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf('\n', Math.Max(0, maxChars - 80));
        if (cut <= 0) cut = Math.Max(0, maxChars - 80);
        var shown = text[..cut].Count(c => c == '\n');
        return text[..cut] + $"\n[truncated after {shown} lines: use fewer fields, a smaller limit, or revit_export]\n";
    }
}
