using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin;

/// <summary>Finding elements and turning them into compact display rows.</summary>
public static class Elements
{
    public static readonly string[] DefaultFields = ["Id", "Family", "Type", "Level"];
    public static readonly string[] DefaultTypeFields = ["Id", "Family", "Name"];

    private static readonly Dictionary<BuiltInCategory, string[]> CategoryDefaults = new()
    {
        [BuiltInCategory.OST_Sheets] = ["Id", "Sheet Number", "Sheet Name"],
        [BuiltInCategory.OST_Views] = ["Id", "Name", "ViewType"],
        [BuiltInCategory.OST_Levels] = ["Id", "Name", "Elevation"],
        [BuiltInCategory.OST_Grids] = ["Id", "Name"],
        [BuiltInCategory.OST_Rooms] = ["Id", "Number", "Name", "Level", "Area"],
        [BuiltInCategory.OST_Areas] = ["Id", "Number", "Name", "Level", "Area"],
        [BuiltInCategory.OST_Schedules] = ["Id", "Name"],
        [BuiltInCategory.OST_Materials] = ["Id", "Name"],
    };

    public static string[] Defaults(Category cat, bool types) =>
        types ? DefaultTypeFields : CategoryDefaults.GetValueOrDefault(cat.BuiltInCategory, DefaultFields);

    /// <summary>Resolves "Walls", "wall", "OST_Walls" to a category, or throws with suggestions.</summary>
    public static Category ResolveCategory(Document doc, string name)
    {
        var cats = doc.Settings.Categories.Cast<Category>().ToList();
        if (Enum.TryParse<BuiltInCategory>(name, true, out var bic) && Category.GetCategory(doc, bic) is { } byEnum)
            return byEnum;
        foreach (var candidate in new[] { name, name + "s", name.TrimEnd('s'), name + "es" })
        {
            var hit = cats.FirstOrDefault(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        var close = cats.Where(c => c.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                    || name.Contains(c.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Name).Take(8).ToList();
        throw new ArgumentException($"No category '{name}'." + (close.Count > 0 ? " Close: " + string.Join(", ", close) : " Use revit_query mode=summary to see categories."));
    }

    /// <summary>Elements in scope (model | view | selection), optionally limited to a category and ids.</summary>
    public static List<Element> Collect(Document doc, UIDocument ui, string? scope, Category? cat, bool types, IReadOnlyCollection<long>? ids = null)
    {
        scope = (scope ?? "model").ToLowerInvariant();
        IEnumerable<Element> items;
        if (ids is { Count: > 0 })
        {
            items = ids.Select(i => doc.GetElement(new ElementId(i))).Where(e => e != null);
        }
        else
        {
            switch (scope)
            {
                case "selection":
                    items = ui.Selection.GetElementIds().Select(doc.GetElement).Where(e => e != null);
                    break;
                case "view":
                    if (types) throw new ArgumentException("scope=view applies to instances, not types.");
                    items = Filtered(new FilteredElementCollector(doc, doc.ActiveView.Id), cat, false);
                    break;
                case "model":
                    items = Filtered(new FilteredElementCollector(doc), cat, types);
                    break;
                default:
                    throw new ArgumentException("scope must be model, view or selection.");
            }
        }
        if (cat != null) items = items.Where(e => e.Category?.Id == cat.Id);
        if (ids is { Count: > 0 } || scope == "selection") items = items.Where(e => (e is ElementType) == types);
        return items.OrderBy(e => e.Id.Value).ToList();
    }

    private static IEnumerable<Element> Filtered(FilteredElementCollector c, Category? cat, bool types)
    {
        if (cat != null) c = c.OfCategoryId(cat.Id);
        c = types ? c.WhereElementIsElementType() : c.WhereElementIsNotElementType();
        return c.ToElements();
    }

    public static Row ToRow(Element e, IEnumerable<string> fields)
    {
        var row = new Row();
        foreach (var f in fields) row[f] = Field(e, f);
        return row;
    }

    /// <summary>A named column: a few computed ones, then instance parameter, then type parameter.</summary>
    public static string? Field(Element e, string name)
    {
        var doc = e.Document;
        switch (name.ToLowerInvariant())
        {
            case "id": return e.Id.Value.ToString();
            case "name": return e.Name;
            case "category": return e.Category?.Name;
            case "typeid": return e.GetTypeId() is { } t && t != ElementId.InvalidElementId ? t.Value.ToString() : null;
            case "type": return e is ElementType ? e.Name : doc.GetElement(e.GetTypeId())?.Name;
            case "family": return (e as ElementType ?? doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName;
            case "level": return LevelName(e);
            case "viewtype": return (e as View)?.ViewType.ToString();
            case "location": return Location(e);
            case "pinned": return e.Pinned ? "yes" : null;
        }
        var p = e.LookupParameter(name)
                ?? (e is ElementType ? null : doc.GetElement(e.GetTypeId())?.LookupParameter(name));
        return p == null ? null : Display(p);
    }

    public static string? Display(Parameter p)
    {
        if (!p.HasValue) return null;
        switch (p.StorageType)
        {
            case StorageType.String:
                return p.AsString();
            case StorageType.ElementId:
                var id = p.AsElementId();
                if (id == ElementId.InvalidElementId) return null;
                return p.Element?.Document.GetElement(id)?.Name ?? p.AsValueString() ?? id.Value.ToString();
            case StorageType.Integer:
                return p.AsValueString() ?? p.AsInteger().ToString();
            case StorageType.Double:
                return p.AsValueString() ?? Table.Num(p.AsDouble());
            default:
                return p.AsValueString();
        }
    }

    private static string? LevelName(Element e)
    {
        var doc = e.Document;
        if (e.LevelId != ElementId.InvalidElementId) return doc.GetElement(e.LevelId)?.Name;
        foreach (var bip in new[] { BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.FAMILY_LEVEL_PARAM,
                                     BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.LEVEL_PARAM })
        {
            var p = e.get_Parameter(bip);
            if (p?.AsElementId() is { } id && id != ElementId.InvalidElementId) return doc.GetElement(id)?.Name;
        }
        return null;
    }

    private static string? Location(Element e) => e.Location switch
    {
        LocationPoint lp => Point(e.Document, lp.Point),
        LocationCurve lc => Point(e.Document, lc.Curve.GetEndPoint(0)) + " > " + Point(e.Document, lc.Curve.GetEndPoint(1)),
        _ => null,
    };

    /// <summary>A point or offset in the model's display units, e.g. (12' 6", 4' 0", 0' 0").</summary>
    public static string Point(Document doc, XYZ p) =>
        $"({Length(doc, p.X)}, {Length(doc, p.Y)}, {Length(doc, p.Z)})";

    public static string Length(Document doc, double feet) =>
        UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, feet, false);

    /// <summary>All parameters of an element (instance, then type with a "type." prefix), values only.</summary>
    public static Row AllParameters(Element e)
    {
        var row = new Row
        {
            ["Id"] = e.Id.Value.ToString(),
            ["Category"] = e.Category?.Name,
            ["Family"] = Field(e, "Family"),
            ["Type"] = Field(e, "Type"),
            ["Level"] = LevelName(e),
            ["Location"] = Location(e),
        };
        foreach (var p in e.Parameters.Cast<Parameter>().OrderBy(p => p.Definition.Name))
            row.TryAdd(p.Definition.Name, Display(p));
        if (e is not ElementType && e.Document.GetElement(e.GetTypeId()) is { } type)
            foreach (var p in type.Parameters.Cast<Parameter>().OrderBy(p => p.Definition.Name))
                row.TryAdd("type." + p.Definition.Name, Display(p));
        return row;
    }

    /// <summary>A view by id, exact name, or sheet number; null = active view.</summary>
    public static View ResolveView(Document doc, string? idOrName)
    {
        if (string.IsNullOrWhiteSpace(idOrName)) return doc.ActiveView;
        if (long.TryParse(idOrName, out var id) && doc.GetElement(new ElementId(id)) is View byId) return byId;
        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
        return views.FirstOrDefault(v => string.Equals(v.Name, idOrName, StringComparison.OrdinalIgnoreCase))
               ?? views.OfType<ViewSheet>().FirstOrDefault(s =>
                   string.Equals(s.SheetNumber, idOrName, StringComparison.OrdinalIgnoreCase)
                   || string.Equals($"{s.SheetNumber} - {s.Name}", idOrName, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No view or sheet '{idOrName}'. Use revit_query category=Views or Sheets.");
    }

    public static string LengthUnit(Document doc) =>
        LabelUtils.GetLabelForUnit(doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId());
}
