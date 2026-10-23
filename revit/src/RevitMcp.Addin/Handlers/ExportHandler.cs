using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

/// <summary>Big outputs go to disk; the model only sees the path and a count.</summary>
public static class ExportHandler
{
    public static CallResponse Run(Document doc, UIDocument ui, Args args, McpConfig config)
    {
        var action = args.Str("action")?.ToLowerInvariant() ?? throw new ArgumentException("action is required.");
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return action switch
        {
            "csv" => Csv(doc, ui, args, config, stamp),
            "schedule" => Schedules(doc, args, config, stamp),
            "pdf" => Pdf(doc, args, config, stamp),
            "dwg" => Dwg(doc, args, config, stamp),
            "image" => Images(doc, args, config),
            _ => CallResponse.Fail("Error: action must be csv, schedule, pdf, dwg or image."),
        };
    }

    private static CallResponse Csv(Document doc, UIDocument ui, Args args, McpConfig config, string stamp)
    {
        var (title, rows, fields, defaults) = QueryHandler.Rows(doc, ui, args, paged: false, out _, out _, out var total);
        var cols = Table.Columns(rows, fields, defaults);
        var file = OutFile(args, config, $"{Safe(title)}_{stamp}.csv");
        var sb = new StringBuilder();
        sb.AppendJoin(',', cols.Select(Quote)).Append("\r\n");
        foreach (var r in rows) sb.AppendJoin(',', cols.Select(c => Quote(r.GetValueOrDefault(c)))).Append("\r\n");
        // BOM so Excel opens non-ASCII names correctly.
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return CallResponse.Success($"Wrote {total} {title} rows x {cols.Count} columns to {file}");
    }

    private static CallResponse Schedules(Document doc, Args args, McpConfig config, string stamp)
    {
        var dir = OutDir(args, config);
        var written = new List<string>();
        foreach (var v in Views(doc, args))
        {
            if (v is not ViewSchedule s) throw new ArgumentException($"{v.Name} is not a schedule.");
            var name = $"{Safe(s.Name)}_{stamp}.csv";
            s.Export(dir, name, new ViewScheduleExportOptions { FieldDelimiter = ",", TextQualifier = ExportTextQualifier.DoubleQuote });
            written.Add(Path.Combine(dir, name));
        }
        return CallResponse.Success($"Wrote {written.Count} schedule(s):\n" + string.Join("\n", written));
    }

    private static CallResponse Pdf(Document doc, Args args, McpConfig config, string stamp)
    {
        var dir = OutDir(args, config);
        var views = Views(doc, args);
        var name = $"{Safe(doc.Title)}_{stamp}";
        var opts = new PDFExportOptions { FileName = name, Combine = true };
        if (!doc.Export(dir, views.Select(v => v.Id).ToList(), opts))
            return CallResponse.Fail("Error: Revit reported the PDF export failed.");
        return CallResponse.Success($"Wrote {views.Count} view(s) to {Path.Combine(dir, name + ".pdf")}");
    }

    private static CallResponse Dwg(Document doc, Args args, McpConfig config, string stamp)
    {
        var dir = OutDir(args, config);
        var views = Views(doc, args);
        var name = $"{Safe(doc.Title)}_{stamp}";
        var before = Directory.GetFiles(dir, "*.dwg").ToHashSet();
        if (!doc.Export(dir, name, views.Select(v => v.Id).ToList(), new DWGExportOptions { MergedViews = false }))
            return CallResponse.Fail("Error: Revit reported the DWG export failed.");
        var files = Directory.GetFiles(dir, "*.dwg").Where(f => !before.Contains(f)).ToList();
        return CallResponse.Success($"Wrote {files.Count} DWG file(s) to {dir}:\n" + string.Join("\n", files.Select(Path.GetFileName)));
    }

    private static CallResponse Images(Document doc, Args args, McpConfig config)
    {
        var dir = OutDir(args, config);
        var views = Views(doc, args);
        var before = Directory.GetFiles(dir, "*.png").ToHashSet();
        foreach (var v in views) ViewHandler.ExportPng(doc, v, 4096, Path.Combine(dir, Safe(doc.Title)));
        var files = Directory.GetFiles(dir, "*.png").Where(f => !before.Contains(f)).ToList();
        return CallResponse.Success($"Wrote {files.Count} PNG file(s) to {dir}:\n" + string.Join("\n", files.Select(Path.GetFileName)));
    }

    private static List<View> Views(Document doc, Args args)
    {
        var names = args.List("views");
        if (names.Count == 0) return [doc.ActiveView];
        return names.Select(n => Elements.ResolveView(doc, n)).ToList();
    }

    private static string OutDir(Args args, McpConfig config)
    {
        var p = args.Str("path");
        var dir = p != null && Path.HasExtension(p) ? Path.GetDirectoryName(p)! : p ?? config.ExportDir ?? McpPaths.DefaultExportDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string OutFile(Args args, McpConfig config, string defaultName)
    {
        var p = args.Str("path");
        var dir = OutDir(args, config);
        return p != null && Path.HasExtension(p) ? Path.GetFullPath(p) : Path.Combine(dir, defaultName);
    }

    private static string Safe(string s) => Core.Csv.SafeFileName(s);

    private static string Quote(string? v) => Core.Csv.Quote(v);
}
