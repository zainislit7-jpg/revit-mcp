using System.Text;

namespace AcadMcp.Core;

/// <summary>One queryable thing (entity, layer, block...). Values are computed lazily per field.</summary>
public interface IRow
{
    string? Get(string field);

    /// <summary>Fields this row can report, in display order. Used by mode=get.</summary>
    IEnumerable<string> Fields { get; }
}

public sealed class DictRow(IReadOnlyDictionary<string, string?> values) : IRow
{
    public string? Get(string field) =>
        values.TryGetValue(field, out var v) ? v
        : values.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value;

    public IEnumerable<string> Fields => values.Keys;
}

/// <summary>Token-lean query shapes: counts first, paged lists, full detail for a few ids.</summary>
public static class Query
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    /// <summary>Resolves a comma list of field names against the known set (case-insensitive).</summary>
    public static IReadOnlyList<string> Fields(string? requested, IReadOnlyList<string> defaults, IReadOnlyCollection<string> known)
    {
        if (string.IsNullOrWhiteSpace(requested)) return defaults;
        if (requested.Trim() == "*") return known.ToList();
        var result = new List<string>();
        foreach (var f in Args.SplitList(requested))
        {
            var canon = known.FirstOrDefault(k => string.Equals(k, f, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolError($"Unknown field '{f}'. Fields: {string.Join(", ", known)}.");
            if (!result.Contains(canon)) result.Add(canon);
        }
        return result;
    }

    public static int Offset(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        var c = cursor.Trim();
        if (c.StartsWith("next:", StringComparison.OrdinalIgnoreCase)) c = c[5..];
        return int.TryParse(c, out var n) && n >= 0 ? n : throw new ToolError($"Bad cursor '{cursor}'. Pass the number after next:.");
    }

    public static string List(string title, IEnumerable<IRow> rows, Filter filter, IReadOnlyList<string> fields,
        int limit, int offset, int maxChars, string? note = null)
    {
        var table = new PipeTable(fields);
        int total = 0, shown = 0;
        var full = false;
        foreach (var row in rows)
        {
            if (!filter.Matches(row.Get)) continue;
            total++;
            if (total <= offset || full || shown >= limit) continue;
            if (table.TryAdd(fields.Select(row.Get), maxChars - 200)) shown++;
            else full = true;
        }

        var sb = new StringBuilder();
        if (total == 0)
        {
            sb.Append($"{title}: 0 matching");
            if (!filter.IsEmpty) sb.Append(" (check the filter, or use mode=summary to see what exists)");
        }
        else if (shown == 0)
        {
            sb.Append($"{title}: {Fmt.Count(total)}, cursor {offset} is past the end");
        }
        else
        {
            var last = offset + shown;
            sb.Append($"{title}: {Fmt.Count(total)} (showing {offset + 1}-{last})");
            if (last < total) sb.Append($" next:{last}");
        }
        if (note is not null) sb.Append(" · ").Append(note);
        if (shown > 0) sb.Append('\n').Append(table);
        return sb.ToString();
    }

    public static string Summary(string title, IEnumerable<IRow> rows, Filter filter, string groupBy, int limit, string? note = null)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var row in rows)
        {
            if (!filter.Matches(row.Get)) continue;
            total++;
            var key = row.Get(groupBy);
            if (string.IsNullOrEmpty(key)) key = "(none)";
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var sb = new StringBuilder($"{title}: {Fmt.Count(total)} in {Fmt.Count(counts.Count)} {groupBy} groups");
        if (note is not null) sb.Append(" · ").Append(note);
        if (total == 0) return sb.ToString();

        var table = new PipeTable([groupBy, "Count"]);
        var ordered = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var kv in ordered.Take(limit)) table.TryAdd([kv.Key, Fmt.Count(kv.Value)], int.MaxValue);
        sb.Append('\n').Append(table);
        if (ordered.Count > limit)
        {
            var rest = ordered.Skip(limit).ToList();
            sb.Append($"\n(+{rest.Count} more groups, {Fmt.Count(rest.Sum(kv => kv.Value))} items; raise limit or filter)");
        }
        return sb.ToString();
    }

    /// <summary>One line per row: <c>ID TYPE Key=Value; Key=Value</c>, empty values dropped.</summary>
    public static string Get(IEnumerable<IRow> rows, IReadOnlyList<string>? fields, int maxChars, string idField, string typeField)
    {
        var sb = new StringBuilder();
        var n = 0;
        foreach (var row in rows)
        {
            var line = new StringBuilder($"{row.Get(idField)} {row.Get(typeField)}");
            var sep = " ";
            foreach (var f in fields ?? row.Fields)
            {
                if (f == idField || f == typeField) continue;
                var v = row.Get(f);
                if (string.IsNullOrEmpty(v)) continue;
                line.Append(sep).Append(f).Append('=').Append(Fmt.Cell(v, 400));
                sep = "; ";
            }
            if (sb.Length + line.Length > maxChars - 100)
            {
                sb.Append($"\n(stopped after {n} items to stay small; ask for fewer ids or fewer fields)");
                break;
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
            n++;
        }
        return sb.Length == 0 ? "No items found for those ids." : sb.ToString();
    }
}
