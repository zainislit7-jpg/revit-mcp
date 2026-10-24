using System.Globalization;
using System.Text;
using AcadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcp.Addin;

/// <summary>The six tools, implemented against the AutoCAD API. Always called on the UI thread.</summary>
sealed partial class AcadHost : IAcadHost
{
    static DocumentCollection Docs => AcApp.DocumentManager;

    static string ShortName(Document d) => Path.GetFileName(d.Name);

    static Document ResolveDoc(Args a)
    {
        var name = a.Str("doc");
        if (name is null) return Docs.MdiActiveDocument ?? throw new ToolError("No drawing is open in AutoCAD.");
        foreach (Document d in Docs)
        {
            if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ShortName(d), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(d.Name), name, StringComparison.OrdinalIgnoreCase))
                return d;
        }
        throw new ToolError($"No open drawing named '{name}'. Open: {string.Join(", ", Docs.Cast<Document>().Select(ShortName))}.");
    }

    static void EnsureIdle(Document doc)
    {
        if (!string.IsNullOrEmpty(doc.CommandInProgress))
            throw new ToolError($"AutoCAD is in the {doc.CommandInProgress} command. Finish it or press Esc, then retry.");
    }

    static string UnitsOf(Database db) => Units.Name((int)db.Insunits);

    static ObjectId IdOf(Database db, string handle) => AcadHostIds.IdOf(db, handle);
}

static class AcadHostIds
{
    public static ObjectId IdOf(Database db, string handle)
    {
        if (!long.TryParse(handle.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
            throw new ToolError($"'{handle}' is not a handle (hex like 2A3F).");
        if (!db.TryGetObjectId(new Handle(h), out var id) || id.IsErased)
            throw new ToolError($"No object with handle {handle} in {Path.GetFileName(db.Filename)}.");
        return id;
    }
}

sealed partial class AcadHost
{

    /// <summary>Name of the layout that owns a space block, or the block's own name for block definitions.</summary>
    static string SpaceName(ObjectId ownerId, Transaction tr)
    {
        if (tr.GetObject(ownerId, OpenMode.ForRead) is not BlockTableRecord btr) return "";
        if (!btr.IsLayout) return btr.Name;
        return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
    }

    static List<Layout> Layouts(Database db, Transaction tr)
    {
        var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        var list = new List<Layout>();
        foreach (DBDictionaryEntry e in dict) list.Add((Layout)tr.GetObject(e.Value, OpenMode.ForRead));
        return list.OrderBy(l => l.TabOrder).ToList();
    }

    public ToolOutput Status(Args a, McpConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"AutoCAD {AcApp.Version} · AcadMcp {Plugin.Version} · {c.ModeLine}");
        var docs = Docs.Cast<Document>().ToList();
        if (docs.Count == 0) return new ToolOutput(sb.Append("No drawings open.").ToString());
        sb.AppendLine("Open: " + string.Join(", ", docs.Select(d => ShortName(d) + (d == Docs.MdiActiveDocument ? " (active)" : ""))));

        var doc = ResolveDoc(a);
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var modelCount = model.Cast<ObjectId>().Count();
        int blocks = 0, xrefs = 0;
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout || btr.IsAnonymous) continue;
            if (btr.IsFromExternalReference) xrefs++; else blocks++;
        }
        var layers = ((LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead)).Cast<ObjectId>().Count();
        var layouts = Layouts(db, tr);
        var current = doc == Docs.MdiActiveDocument ? LayoutManager.Current.CurrentLayout : (db.TileMode ? "Model" : "(paper)");
        var clayer = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;

        sb.AppendLine($"Drawing: {ShortName(doc)}{(doc.IsReadOnly ? " (read-only file)" : "")} · {doc.Name}");
        sb.AppendLine($"Units: {UnitsOf(db)} (INSUNITS={(int)db.Insunits}) · LTSCALE {Fmt.N(db.Ltscale)} · DIMSCALE {Fmt.N(db.Dimscale)} · current layer {clayer}");
        sb.AppendLine($"Model: {Fmt.Count(modelCount)} entities · {layers} layers · {blocks} blocks · {xrefs} xrefs");
        sb.Append($"Layouts ({layouts.Count - 1}): {string.Join(", ", layouts.Where(l => !l.ModelType).Select(l => l.LayoutName))} · current {current}");
        sb.Append($"\nExports go to {c.ExportFolder}");
        return new ToolOutput(sb.ToString());
    }
}
