using System.Text;
using AcadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AcadMcp.Addin;

sealed partial class AcadHost
{
    static readonly string[] CsvEntityDefaults = ["Handle", "Type", "Layer", "Space", "Block", "Text", "Length", "Area", "Pos"];

    public ToolOutput Export(Args a, McpConfig c)
    {
        var action = (a.Str("action") ?? throw new ToolError("action is required.")).ToLowerInvariant();
        var doc = ResolveDoc(a);
        var db = doc.Database;
        var stem = Path.GetFileNameWithoutExtension(doc.Name);

        return action switch
        {
            "csv" => ExportCsv(db, a, OutPath(c, a, $"{stem}-{a.Str("table", "entities")}", ".csv")),
            "attrs" => ExportAttrs(db, a, OutPath(c, a, $"{stem}-attributes", ".csv")),
            "pdf" => ExportPdf(doc, a, OutPath(c, a, stem, ".pdf")),
            "dxf" => SaveCopy(doc, OutPath(c, a, stem, ".dxf"), dxf: true),
            "dwg" => SaveCopy(doc, OutPath(c, a, $"{stem}-copy", ".dwg"), dxf: false),
            _ => throw new ToolError("action must be csv, attrs, pdf, dxf or dwg."),
        };
    }

    static string OutPath(McpConfig c, Args a, string stem, string ext)
    {
        var path = a.Str("path");
        if (path is null) path = Path.Combine(c.ExportFolder, $"{stem}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
        else if (!Path.IsPathRooted(path)) path = Path.Combine(c.ExportFolder, path);
        if (!Path.HasExtension(path)) path += ext;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return Path.GetFullPath(path);
    }

    static ToolOutput ExportCsv(Database db, Args a, string path)
    {
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var src = SourceFor(a.Str("table", "entities"), db, tr);
        var filter = Filter.Parse(a.Str("filter"), src.Known);
        var defaults = src.Title == "Entities" ? CsvEntityDefaults : src.Known;
        var fields = Core.Query.Fields(a.Str("fields"), defaults, src.Known);

        var preview = new PipeTable(fields);
        var rows = 0;
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            Csv.WriteRow(w, fields);
            foreach (var r in src.Rows(filter))
            {
                if (!filter.Matches(r.Get)) continue;
                var cells = fields.Select(r.Get).ToList();
                Csv.WriteRow(w, cells);
                if (rows++ < 3) preview.TryAdd(cells, int.MaxValue);
            }
        }
        return new ToolOutput($"Wrote {Fmt.Count(rows)} {src.Title.ToLowerInvariant()} rows x {fields.Count} columns to {path}" +
                              (rows > 0 ? $"\nFirst rows:\n{preview}" : ""));
    }

    /// <summary>One row per block reference, one column per attribute tag: door/window/room tag schedules.</summary>
    static ToolOutput ExportAttrs(Database db, Args a, string path)
    {
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var filter = Filter.Parse(a.Str("filter"), EntityRow.All);
        var refs = EntityRows(db, tr, filter)
            .Where(r => r.Entity is BlockReference && filter.Matches(r.Get))
            .Select(r => (Row: r, Attrs: EntityRow.Attributes((BlockReference)r.Entity, tr)))
            .Where(x => x.Attrs.Count > 0)
            .ToList();
        if (refs.Count == 0) return new ToolOutput("No block references with attributes match. Try query mode=summary group=Block.");

        var tags = refs.SelectMany(x => x.Attrs.Select(kv => kv.Key)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string[] fixedCols = ["Handle", "Block", "Layer", "Space", "Pos", "Rot"];
        using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            Csv.WriteRow(w, fixedCols.Concat(tags));
            foreach (var (row, attrs) in refs)
            {
                var map = attrs.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
                Csv.WriteRow(w, fixedCols.Select(row.Get).Concat(tags.Select(t => map.GetValueOrDefault(t))));
            }
        }
        var byBlock = refs.GroupBy(x => x.Row.Get("Block")).OrderByDescending(g => g.Count()).Take(10)
            .Select(g => $"{g.Key} {g.Count()}");
        return new ToolOutput($"Wrote {Fmt.Count(refs.Count)} block references x {tags.Count} attribute columns to {path}\n" +
                              $"Blocks: {string.Join(", ", byBlock)}\nTags: {string.Join(", ", tags.Take(30))}");
    }

    static ToolOutput SaveCopy(Document doc, string path, bool dxf)
    {
        using (doc.LockDocument())
        {
            if (dxf)
                doc.Database.DxfOut(path, 16, DwgVersion.Current);
            else
            {
                // Wblock gives an independent copy, so the open drawing keeps its own file name.
                using var copy = doc.Database.Wblock();
                copy.SaveAs(path, DwgVersion.Current);
            }
        }
        return new ToolOutput($"Saved {(dxf ? "DXF" : "DWG")} copy of {ShortName(doc)} to {path} ({new FileInfo(path).Length / 1024:N0} KB).");
    }

    const string PdfDevice = "DWG To PDF.pc3";

    /// <summary>Plots layouts with their own page setups into one multi-sheet PDF.</summary>
    static ToolOutput ExportPdf(Document doc, Args a, string path)
    {
        if (doc != Docs.MdiActiveDocument) throw new ToolError($"{ShortName(doc)} must be the active drawing to plot. Use view action=activate.");
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting) throw new ToolError("AutoCAD is already plotting. Try again shortly.");
        EnsureIdle(doc);

        var db = doc.Database;
        var notes = new List<string>();
        var oldBackground = AcApp.GetSystemVariable("BACKGROUNDPLOT");
        string oldLayout;
        using var docLock = doc.LockDocument();
        oldLayout = LayoutManager.Current.CurrentLayout;
        try
        {
            AcApp.SetSystemVariable("BACKGROUNDPLOT", 0);
            using var tr = db.TransactionManager.StartTransaction();
            var all = Layouts(db, tr);
            var requested = Args.SplitList(a.Str("layouts"));
            var layouts = requested.Count == 0
                ? all.Where(l => l.LayoutName == oldLayout).ToList()
                : requested.Count == 1 && requested[0] == "*"
                    ? all.Where(l => !l.ModelType).ToList()
                    : requested.Select(n => all.FirstOrDefault(l => string.Equals(l.LayoutName, n, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ToolError($"No layout '{n}'. Layouts: {string.Join(", ", all.Select(l => l.LayoutName))}.")).ToList();
            if (layouts.Count == 0) throw new ToolError("No layouts to plot.");

            var psv = PlotSettingsValidator.Current;
            using var engine = PlotFactory.CreatePublishEngine();
            using var progress = new PlotProgressDialog(false, layouts.Count, true);
            progress.LowerPlotProgressRange = 0;
            progress.UpperPlotProgressRange = 100;
            progress.OnBeginPlot();
            progress.IsVisible = true;
            engine.BeginPlot(progress, null);

            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                LayoutManager.Current.CurrentLayout = layout.LayoutName;
                var ps = new PlotSettings(layout.ModelType);
                ps.CopyFrom(layout);
                if (!layout.PlotConfigurationName.Contains("pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var media = MatchMedia(psv, ps, layout);
                    notes.Add($"{layout.LayoutName}: page setup uses {layout.PlotConfigurationName}, plotted with {PdfDevice} on {media}");
                }
                psv.RefreshLists(ps);

                var info = new PlotInfo { Layout = layout.ObjectId, OverrideSettings = ps };
                new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled }.Validate(info);
                if (i == 0) engine.BeginDocument(info, doc.Name, null, 1, true, path);

                progress.OnBeginSheet();
                progress.SheetProgressPos = 0;
                engine.BeginPage(new PlotPageInfo(), info, i == layouts.Count - 1, null);
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                engine.EndPage(null);
                progress.SheetProgressPos = 100;
                progress.OnEndSheet();
            }
            engine.EndDocument(null);
            progress.PlotProgressPos = 100;
            progress.OnEndPlot();
            engine.EndPlot(null);
            tr.Commit();

            var size = File.Exists(path) ? $" ({new FileInfo(path).Length / 1024:N0} KB)" : "";
            return new ToolOutput($"Plotted {layouts.Count} sheet(s) [{string.Join(", ", layouts.Select(l => l.LayoutName))}] to {path}{size}" +
                                  (notes.Count > 0 ? "\n" + string.Join("\n", notes) : ""));
        }
        finally
        {
            AcApp.SetSystemVariable("BACKGROUNDPLOT", oldBackground);
            try { LayoutManager.Current.CurrentLayout = oldLayout; } catch (Autodesk.AutoCAD.Runtime.Exception) { /* layout gone */ }
        }
    }

    /// <summary>Switch to the PDF device, keeping the same paper if it exists there, else the nearest size.</summary>
    static string MatchMedia(PlotSettingsValidator psv, PlotSettings ps, Layout layout)
    {
        psv.SetPlotConfigurationName(ps, PdfDevice, null);
        psv.RefreshLists(ps);
        var names = psv.GetCanonicalMediaNameList(ps).Cast<string>().ToList();
        if (names.Contains(layout.CanonicalMediaName))
        {
            psv.SetCanonicalMediaName(ps, layout.CanonicalMediaName);
            return layout.CanonicalMediaName;
        }
        var want = layout.PlotPaperSize;
        string best = names.FirstOrDefault() ?? throw new ToolError($"{PdfDevice} lists no paper sizes.");
        var bestScore = double.MaxValue;
        foreach (var n in names)
        {
            psv.SetCanonicalMediaName(ps, n);
            var s = ps.PlotPaperSize;
            var score = Math.Min(Math.Abs(s.X - want.X) + Math.Abs(s.Y - want.Y), Math.Abs(s.X - want.Y) + Math.Abs(s.Y - want.X));
            if (score < bestScore) (best, bestScore) = (n, score);
        }
        psv.SetCanonicalMediaName(ps, best);
        return best;
    }
}
