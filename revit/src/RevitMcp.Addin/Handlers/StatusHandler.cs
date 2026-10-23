using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

public static class StatusHandler
{
    public static CallResponse Run(UIApplication app, McpConfig config)
    {
        var a = app.Application;
        var sb = new StringBuilder();
        sb.Append($"Revit {a.VersionName} ({a.VersionBuild}), add-in {App.Version}\n");
        sb.Append($"Writes: {(config.AllowWrite ? "on" : "off")}  Scripts: {(config.AllowScripts ? "on" : "off")}\n");

        var ui = app.ActiveUIDocument;
        if (ui == null)
        {
            sb.Append("No model open.\n");
            return CallResponse.Success(sb.ToString());
        }

        var doc = ui.Document;
        sb.Append($"Model: {doc.Title}{(doc.IsModified ? " (unsaved changes)" : "")}");
        if (doc.IsWorkshared) sb.Append(" workshared");
        if (doc.IsFamilyDocument) sb.Append(" family");
        sb.Append($"\nLength units: {Elements.LengthUnit(doc)} (modify lengths are always mm)\n");

        var v = doc.ActiveView;
        sb.Append($"Active view: {v.Name} [{v.ViewType}] id {v.Id.Value}\n");

        var sel = ui.Selection.GetElementIds();
        sb.Append($"Selection: {sel.Count}");
        if (sel.Count > 0)
            sb.Append(" (" + string.Join(", ", sel.Select(doc.GetElement)
                .GroupBy(e => e?.Category?.Name ?? "Other").OrderByDescending(g => g.Count())
                .Take(6).Select(g => $"{g.Key} {g.Count()}")) + ")");
        sb.Append('\n');

        var others = a.Documents.Cast<Document>().Where(d => !d.Equals(doc) && !d.IsLinked).Select(d => d.Title).ToList();
        if (others.Count > 0) sb.Append("Other open models: " + string.Join(", ", others) + "\n");
        return CallResponse.Success(sb.ToString());
    }
}
