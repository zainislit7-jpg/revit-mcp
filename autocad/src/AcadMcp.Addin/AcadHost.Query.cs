using AcadMcp.Core;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcp.Addin;

sealed partial class AcadHost
{
    public ToolOutput Query(Args a, McpConfig c)
    {
        var doc = ResolveDoc(a);
        var db = doc.Database;
        var mode = a.Str("mode", "summary").ToLowerInvariant();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var src = SourceFor(a.Str("table", "entities"), db, tr);
        var filter = Filter.Parse(a.Str("filter"), src.Known);
        var note = $"units {UnitsOf(db)}";

        var text = mode switch
        {
            "summary" when src.Title.StartsWith("Entities", StringComparison.Ordinal) => Core.Query.Summary(
                src.Title, src.Rows(filter), filter,
                Core.Query.Fields(a.Str("group"), [src.DefaultGroup], src.Known)[0],
                a.Int("limit", 30, 1, Core.Query.MaxLimit), note),
            "summary" or "list" => Core.Query.List(
                src.Title, src.Rows(filter), filter,
                Core.Query.Fields(a.Str("fields"), src.Defaults, src.Known),
                a.Int("limit", Core.Query.DefaultLimit, 1, Core.Query.MaxLimit),
                Core.Query.Offset(a.Str("cursor")), c.MaxChars, src.Title.StartsWith("Entities", StringComparison.Ordinal) ? note : null),
            "get" => Core.Query.Get(
                src.ByIds(a.List("ids") is { Count: > 0 } ids ? ids : throw new ToolError("mode=get needs ids (handles or names).")),
                a.Str("fields") is null ? null : Core.Query.Fields(a.Str("fields"), src.Defaults, src.Known),
                c.MaxChars, src.IdField, src.TypeField),
            _ => throw new ToolError("mode must be summary, list or get."),
        };
        return new ToolOutput(text);
    }

    static Source SourceFor(string table, Database db, Transaction tr) => table.ToLowerInvariant() switch
    {
        "entities" or "entity" => Entities(db, tr),
        "layers" or "layer" => LayerRows(db, tr),
        "blocks" or "block" => BlockRows(db, tr, xrefs: false),
        "xrefs" or "xref" => BlockRows(db, tr, xrefs: true),
        "layouts" or "layout" => LayoutRows(db, tr),
        "textstyles" or "textstyle" => TextStyleRows(db, tr),
        "dimstyles" or "dimstyle" => DimStyleRows(db, tr),
        _ => throw new ToolError($"Unknown table '{table}'. Use entities, layers, blocks, layouts, xrefs, textstyles or dimstyles."),
    };

    static Source Entities(Database db, Transaction tr) => new(
        "Entities", EntityRow.All, EntityRow.Defaults, "Handle", "Type", "Type",
        f => EntityRows(db, tr, f),
        ids => ids.Select(h =>
        {
            var id = IdOf(db, h);
            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity ?? throw new ToolError($"{h} is not an entity.");
            return (IRow)new EntityRow(ent, SpaceName(ent.OwnerId, tr), tr);
        }));

    /// <summary>Entities of the spaces named by the filter's Space clause (default Model only).</summary>
    internal static IEnumerable<EntityRow> EntityRows(Database db, Transaction tr, Filter f)
    {
        var wanted = f.ValuesFor("Space") ?? (f.Mentions("Space") ? ["*"] : ["Model"]);
        foreach (var layout in Layouts(db, tr))
        {
            if (!wanted.Any(w => Wildcard.Match(w, layout.LayoutName))) continue;
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
                if (tr.GetObject(id, OpenMode.ForRead) is Entity e)
                    yield return new EntityRow(e, layout.LayoutName, tr);
        }
    }

    static Source Table(string title, IReadOnlyList<string> known, IReadOnlyList<string> defaults, string typeField,
        Func<IEnumerable<FuncRow>> all) => new(
        title, known, defaults, "Name", typeField, known[0],
        _ => all(),
        names => all().Where(r => names.Any(n => Wildcard.Match(n, r.Get("Name") ?? ""))));

    static Source LayerRows(Database db, Transaction tr)
    {
        // Entity counts per layer, computed only if Count is asked for.
        var counts = new Lazy<Dictionary<string, int>>(() =>
            EntityRows(db, tr, Filter.Parse("Space=*")).GroupBy(r => r.Entity.Layer, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
        return Table("Layers",
            ["Name", "On", "Frozen", "Locked", "Plot", "Color", "Linetype", "Lineweight", "Current", "Count", "Description"],
            ["Name", "On", "Frozen", "Locked", "Color"], "Color",
            () => ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Cast<ObjectId>()
                .Select(id => (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead))
                .Select(l => new FuncRow([
                    ("Name", () => l.Name),
                    ("On", () => Fmt.Bool(!l.IsOff)),
                    ("Frozen", () => Fmt.Bool(l.IsFrozen)),
                    ("Locked", () => Fmt.Bool(l.IsLocked)),
                    ("Plot", () => Fmt.Bool(l.IsPlottable)),
                    ("Color", () => EntityRow.ColorText(l.Color)),
                    ("Linetype", () => ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name),
                    ("Lineweight", () => EntityRow.Lw(l.LineWeight)),
                    ("Current", () => Fmt.Bool(l.ObjectId == db.Clayer)),
                    ("Count", () => counts.Value.GetValueOrDefault(l.Name).ToString()),
                    ("Description", () => l.Description),
                ])));
    }

    static Source BlockRows(Database db, Transaction tr, bool xrefs)
    {
        static int Refs(BlockTableRecord b, Transaction tr)
        {
            var n = b.GetBlockReferenceIds(true, false).Count;
            foreach (ObjectId anon in b.GetAnonymousBlockIds()) // dynamic block instances
                n += ((BlockTableRecord)tr.GetObject(anon, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count;
            return n;
        }

        IEnumerable<BlockTableRecord> Defs() => ((BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead)).Cast<ObjectId>()
            .Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Where(b => !b.IsLayout && !b.IsAnonymous && b.IsFromExternalReference == xrefs);

        if (xrefs)
            return Table("Xrefs", ["Name", "Path", "Status", "Overlay", "Refs"], ["Name", "Path", "Status", "Refs"], "Status",
                () => Defs().Select(b => new FuncRow([
                    ("Name", () => b.Name),
                    ("Path", () => b.PathName),
                    ("Status", () => b.XrefStatus.ToString()),
                    ("Overlay", () => Fmt.Bool(b.IsFromOverlayReference)),
                    ("Refs", () => Refs(b, tr).ToString()),
                ])));

        return Table("Blocks", ["Name", "Refs", "Attdefs", "Dynamic", "Units", "Description"], ["Name", "Refs", "Attdefs"], "Dynamic",
            () => Defs().Select(b => new FuncRow([
                ("Name", () => b.Name),
                ("Refs", () => Refs(b, tr).ToString()),
                ("Attdefs", () => string.Join(",", b.Cast<ObjectId>().Select(id => tr.GetObject(id, OpenMode.ForRead))
                    .OfType<AttributeDefinition>().Where(ad => !ad.Constant).Select(ad => ad.Tag))),
                ("Dynamic", () => Fmt.Bool(b.IsDynamicBlock)),
                ("Units", () => b.Units.ToString()),
                ("Description", () => b.Comments),
            ])));
    }

    static Source LayoutRows(Database db, Transaction tr) => Table("Layouts",
        ["Name", "Tab", "Device", "Media", "Entities", "Viewports"], ["Name", "Tab", "Device", "Media"], "Device",
        () => Layouts(db, tr).Select(l => new FuncRow([
            ("Name", () => l.LayoutName),
            ("Tab", () => l.TabOrder.ToString()),
            ("Device", () => l.PlotConfigurationName),
            ("Media", () => l.CanonicalMediaName),
            ("Entities", () => ((BlockTableRecord)tr.GetObject(l.BlockTableRecordId, OpenMode.ForRead)).Cast<ObjectId>().Count().ToString()),
            ("Viewports", () => l.ModelType ? null : Math.Max(0, (l.GetViewports()?.Count ?? 1) - 1).ToString()),
        ])));

    static Source TextStyleRows(Database db, Transaction tr) => Table("Text styles",
        ["Name", "Font", "BigFont", "Height", "Width"], ["Name", "Font", "Height"], "Font",
        () => ((TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead)).Cast<ObjectId>()
            .Select(id => (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Where(s => !s.IsShapeFile)
            .Select(s => new FuncRow([
                ("Name", () => s.Name),
                ("Font", () => string.IsNullOrEmpty(s.FileName) ? s.Font.TypeFace : s.FileName),
                ("BigFont", () => s.BigFontFileName),
                ("Height", () => Fmt.N(s.TextSize)),
                ("Width", () => Fmt.N(s.XScale)),
            ])));

    static Source DimStyleRows(Database db, Transaction tr) => Table("Dim styles",
        ["Name", "Scale", "TextHeight", "ArrowSize", "TextStyle", "Current"], ["Name", "Scale", "TextHeight", "Current"], "Scale",
        () => ((DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead)).Cast<ObjectId>()
            .Select(id => (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Select(d => new FuncRow([
                ("Name", () => d.Name),
                ("Scale", () => Fmt.N(d.Dimscale)),
                ("TextHeight", () => Fmt.N(d.Dimtxt)),
                ("ArrowSize", () => Fmt.N(d.Dimasz)),
                ("TextStyle", () => ((TextStyleTableRecord)tr.GetObject(d.Dimtxsty, OpenMode.ForRead)).Name),
                ("Current", () => Fmt.Bool(d.ObjectId == db.Dimstyle)),
            ])));
}
