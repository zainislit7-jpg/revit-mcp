using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

public static class QueryHandler
{
    private const int SummaryTop = 40;

    public static CallResponse Run(Document doc, UIDocument ui, Args args, McpConfig config)
    {
        var mode = (args.Str("mode") ?? (args.Str("category") != null ? "list" : "summary")).ToLowerInvariant();
        return mode switch
        {
            "summary" => Summary(doc, ui, args),
            "list" => List(doc, ui, args, config),
            "get" => Get(doc, args, config),
            _ => CallResponse.Fail("Error: mode must be summary, list or get."),
        };
    }

    private static CallResponse Summary(Document doc, UIDocument ui, Args args)
    {
        var scope = args.Str("scope");
        var groups = Elements.Collect(doc, ui, scope, null, false)
            .Where(e => e.Category != null)
            .GroupBy(e => e.Category.Name)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Name)
            .ToList();

        var sb = new StringBuilder();
        sb.Append($"{doc.Title}, scope {scope ?? "model"}: {groups.Sum(g => g.Count)} elements in {groups.Count} categories");
        sb.Append(groups.Count > SummaryTop ? $" (top {SummaryTop})\n" : "\n");
        sb.Append("Category|Count\n");
        foreach (var g in groups.Take(SummaryTop)) sb.Append(Table.Cell(g.Name)).Append('|').Append(g.Count).Append('\n');
        return CallResponse.Success(sb.ToString());
    }

    private static CallResponse List(Document doc, UIDocument ui, Args args, McpConfig config)
    {
        var (title, rows, fields, defaults) = Rows(doc, ui, args, paged: true, out var offset, out var limit, out var total);
        return CallResponse.Success(TextResult.Page(title, rows, offset, limit, total, fields, defaults, config.MaxChars));
    }

    /// <summary>
    /// Shared with export. When paged and unfiltered, only the requested page is turned into rows,
    /// so listing 50 of 20,000 walls reads 50 elements' parameters, not 20,000.
    /// </summary>
    public static (string Title, List<Row> Rows, List<string> Fields, string[] Defaults) Rows(
        Document doc, UIDocument ui, Args args, bool paged, out int offset, out int limit, out int total)
    {
        var catName = args.Str("category") ?? throw new ArgumentException("category is required for list and csv.");
        var types = args.Bool("types", false);
        var cat = Elements.ResolveCategory(doc, catName);
        var filter = Filter.Parse(args.Str("filter"));
        var fields = args.List("fields");
        var defaults = Elements.Defaults(cat, types);
        var shown = fields.Count > 0 ? fields : defaults.ToList();
        var load = shown.Concat(filter.Fields).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var elems = Elements.Collect(doc, ui, args.Str("scope"), cat, types, args.Ids());
        (limit, offset) = args.Page();
        if (!paged) (offset, limit) = (0, int.MaxValue);

        List<Row> rows;
        if (filter.IsEmpty)
        {
            total = elems.Count;
            rows = elems.Skip(offset).Take(limit).Select(e => Elements.ToRow(e, load)).ToList();
        }
        else
        {
            var all = elems.Select(e => Elements.ToRow(e, load)).Where(filter.Match).ToList();
            total = all.Count;
            rows = all.Skip(offset).Take(limit).ToList();
        }
        return (cat.Name + (types ? " types" : ""), rows, shown, defaults);
    }

    private static CallResponse Get(Document doc, Args args, McpConfig config)
    {
        var ids = args.Ids();
        if (ids.Count == 0) return CallResponse.Fail("Error: get needs ids, e.g. ids=\"312,4410\".");
        var fields = args.List("fields");
        var sb = new StringBuilder();
        foreach (var id in ids.Take(20))
        {
            var e = doc.GetElement(new ElementId(id));
            if (e == null) { sb.Append($"# {id}: not found\n"); continue; }
            var row = fields.Count > 0 ? Elements.ToRow(e, fields) : Elements.AllParameters(e);
            sb.Append($"# {id}\n").Append(Table.RenderRecord(row, fields.Count > 0 ? fields : null));
        }
        if (ids.Count > 20) sb.Append($"[only the first 20 of {ids.Count} ids; use list with fields for more]\n");
        return CallResponse.Success(TextResult.Cap(sb.ToString(), config.MaxChars));
    }
}
