using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

public static class ViewHandler
{
    public static CallResponse Run(Document doc, UIDocument ui, Args args)
    {
        var action = (args.Str("action") ?? "screenshot").ToLowerInvariant();
        switch (action)
        {
            case "screenshot":
            {
                var view = Elements.ResolveView(doc, args.Str("view"));
                var width = Math.Clamp(args.Int("width", Limits.DefaultImagePx), 128, Limits.MaxImagePx);
                var png = Snapshot(doc, view, width, out _);
                return CallResponse.Success($"{view.Name} [{view.ViewType}] id {view.Id.Value}, {width}px wide", Convert.ToBase64String(png));
            }
            case "activate":
            {
                var view = Elements.ResolveView(doc, args.Str("view") ?? throw new ArgumentException("activate needs view."));
                // RequestViewChange is the switch that is safe from an external event; it happens right after this call.
                ui.RequestViewChange(view);
                return CallResponse.Success($"Opening {view.Name} [{view.ViewType}] id {view.Id.Value}.");
            }
            case "select":
            {
                var ids = ExistingIds(doc, args);
                ui.Selection.SetElementIds(ids);
                return CallResponse.Success($"Selected {ids.Count} elements.");
            }
            case "zoom":
            {
                var ids = ExistingIds(doc, args);
                if (ids.Count == 0) return CallResponse.Fail("Error: zoom needs ids.");
                ui.ShowElements(ids);
                return CallResponse.Success($"Zoomed to {ids.Count} elements.");
            }
            default:
                return CallResponse.Fail("Error: action must be screenshot, activate, select or zoom.");
        }
    }

    private static List<ElementId> ExistingIds(Document doc, Args args) =>
        args.Ids().Select(i => new ElementId(i)).Where(id => doc.GetElement(id) != null).ToList();

    /// <summary>Exports one view to PNG through a private temp folder and returns the bytes.</summary>
    public static byte[] Snapshot(Document doc, View view, int width, out string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ClaudeMcp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ExportPng(doc, view, width, Path.Combine(dir, "view"));
            file = Directory.GetFiles(dir, "*.png").FirstOrDefault()
                   ?? throw new InvalidOperationException($"Revit produced no image for {view.Name}. Schedules and some legends cannot be exported as images.");
            return File.ReadAllBytes(file);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    /// <summary>Revit appends the view name to filePath, so callers pick up whatever PNG lands in the folder.</summary>
    public static void ExportPng(Document doc, View view, int width, string filePath)
    {
        var opts = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = filePath,
            PixelSize = width,
            FitDirection = FitDirectionType.Horizontal,
            ZoomType = ZoomFitType.FitToPage,
            ImageResolution = ImageResolution.DPI_72,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
        };
        opts.SetViewsAndSheets([view.Id]);
        doc.ExportImage(opts);
    }
}
