using System.Drawing.Imaging;
using AcadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Addin;

sealed partial class AcadHost
{
    public ToolOutput View(Args a, McpConfig c)
    {
        var action = (a.Str("action") ?? throw new ToolError("action is required.")).ToLowerInvariant();
        string text;
        Document doc;
        switch (action)
        {
            case "open":
            {
                var path = a.Str("name") ?? a.Str("path") ?? throw new ToolError("open needs name = full path of a .dwg.");
                if (!File.Exists(path)) throw new ToolError($"File not found: {path}");
                // Without write permission, open read-only so nothing can be saved over the file.
                doc = DocumentCollectionExtension.Open(Docs, path, !c.AllowWrite);
                Docs.MdiActiveDocument = doc;
                text = $"Opened {ShortName(doc)}{(c.AllowWrite ? "" : " read-only")}.";
                break;
            }
            case "activate":
                doc = ResolveDoc(new Args(System.Text.Json.JsonSerializer.SerializeToElement(new { doc = a.Str("name") ?? a.Str("doc") })));
                Docs.MdiActiveDocument = doc;
                text = $"Active drawing is now {ShortName(doc)}.";
                break;
            default:
                doc = ResolveDoc(a);
                if (doc != Docs.MdiActiveDocument)
                    throw new ToolError($"{ShortName(doc)} is not the active drawing. Use action=activate first.");
                text = action switch
                {
                    "screenshot" => "",
                    "zoom" => Zoom(doc, a),
                    "select" => Select(doc, a),
                    "layout" => SetLayout(doc, a),
                    "regen" => Regen(doc),
                    _ => throw new ToolError("action must be screenshot, zoom, select, layout, open, activate or regen."),
                };
                break;
        }

        if (action == "screenshot" || a.Bool("shot", false))
        {
            var (png, w, h) = Screenshot(doc, a.Int("width", 768, 256, 2048));
            text = (text.Length > 0 ? text + "\n" : "") + $"Screenshot {w}x{h} of {ShortName(doc)}.";
            return new ToolOutput(text, png);
        }
        return new ToolOutput(text);
    }

    static (byte[] Png, int W, int H) Screenshot(Document doc, int width)
    {
        using var view = doc.Editor.GetCurrentView();
        var ratio = view.Width > 0 ? view.Height / view.Width : 0.625;
        var height = (int)Math.Clamp(Math.Round(width * ratio), 128, 2048);
        using var bmp = doc.CapturePreviewImage((uint)width, (uint)height);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return (ms.ToArray(), bmp.Width, bmp.Height);
    }

    static string Zoom(Document doc, Args a)
    {
        var db = doc.Database;
        Extents3d ext;
        var target = a.Str("target") ?? (a.Str("ids") is not null ? "ids" : a.Str("window") is not null ? "window" : "extents");
        switch (target.ToLowerInvariant())
        {
            case "extents":
                using (doc.LockDocument()) db.UpdateExt(true);
                ext = new Extents3d(db.Extmin, db.Extmax);
                break;
            case "ids":
                ext = ExtentsOf(db, a.List("ids"));
                break;
            case "window":
                var n = Args.SplitList(a.Str("window")).Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                if (n.Count != 4) throw new ToolError("window must be x1,y1,x2,y2.");
                ext = new Extents3d(new Point3d(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), 0), new Point3d(Math.Max(n[0], n[2]), Math.Max(n[1], n[3]), 0));
                break;
            default:
                throw new ToolError("target must be extents, ids or window.");
        }

        var ed = doc.Editor;
        using var view = ed.GetCurrentView();
        // World -> display coordinates of the current view, so the window fits whatever the view direction.
        var wcs2dcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                       Matrix3d.Displacement(view.Target - Point3d.Origin) *
                       Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
        ext.TransformBy(wcs2dcs);
        var w = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, 1e-6) * 1.05;
        var h = Math.Max(ext.MaxPoint.Y - ext.MinPoint.Y, 1e-6) * 1.05;
        var ratio = view.Width / view.Height;
        if (w / h < ratio) w = h * ratio; else h = w / ratio;
        view.CenterPoint = new Point2d((ext.MinPoint.X + ext.MaxPoint.X) / 2, (ext.MinPoint.Y + ext.MaxPoint.Y) / 2);
        view.Width = w;
        view.Height = h;
        ed.SetCurrentView(view);
        return $"Zoomed to {target}.";
    }

    static Extents3d ExtentsOf(Database db, IReadOnlyList<string> handles)
    {
        if (handles.Count == 0) throw new ToolError("ids is empty.");
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        Extents3d? ext = null;
        foreach (var h in handles)
        {
            if (tr.GetObject(IdOf(db, h), OpenMode.ForRead) is not Entity e || e.Bounds is not { } b) continue;
            if (ext is { } x) { x.AddExtents(b); ext = x; } else ext = b;
        }
        return ext ?? throw new ToolError("None of those ids have visible extents.");
    }

    static string Select(Document doc, Args a)
    {
        var ids = a.List("ids").Select(h => IdOf(doc.Database, h)).ToArray();
        if (ids.Length == 0) throw new ToolError("select needs ids.");
        doc.Editor.SetImpliedSelection(ids);
        return $"Selected {ids.Length} objects (pickfirst set).";
    }

    static string SetLayout(Document doc, Args a)
    {
        var name = a.Str("name") ?? throw new ToolError("layout needs name (Model or a layout tab).");
        EnsureIdle(doc);
        using (doc.LockDocument())
            LayoutManager.Current.CurrentLayout = name;
        return $"Current layout: {LayoutManager.Current.CurrentLayout}.";
    }

    static string Regen(Document doc)
    {
        doc.Editor.Regen();
        return "Regenerated.";
    }
}
