using System.Collections;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

/// <summary>Globals visible to revit_run_script code.</summary>
public sealed class ScriptGlobals
{
    internal readonly StringBuilder Output = new();

#pragma warning disable IDE1006 // lower-case names are what the tool description promises
    public required Document doc { get; init; }
    public required UIDocument uidoc { get; init; }
    public required UIApplication app { get; init; }
#pragma warning restore IDE1006

    public void Print(object? value) => Output.Append(ScriptHandler.Format(value)).Append('\n');
}

/// <summary>
/// The "code mode" escape hatch: the model loops and filters inside Revit and returns only
/// the answer. Always runs inside one transaction that is rolled back unless commit is set.
/// </summary>
public static class ScriptHandler
{
    private static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(typeof(Document).Assembly, typeof(UIApplication).Assembly,
                        typeof(Enumerable).Assembly, typeof(ScriptGlobals).Assembly)
        .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Text",
                     "Autodesk.Revit.DB", "Autodesk.Revit.DB.Architecture", "Autodesk.Revit.DB.Structure",
                     "Autodesk.Revit.DB.Mechanical", "Autodesk.Revit.DB.Plumbing", "Autodesk.Revit.DB.Electrical",
                     "Autodesk.Revit.UI");

    public static Script<object> Compile(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("code is required.");
        var script = CSharpScript.Create<object>(code, Options, typeof(ScriptGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new ArgumentException("Script does not compile:\n" + string.Join("\n", errors.Take(10).Select(e => e.ToString())));
        return script;
    }

    public static CallResponse Run(UIApplication app, Script<object> script, bool commit, McpConfig config)
    {
        var ui = app.ActiveUIDocument ?? throw new InvalidOperationException("No model is open in Revit.");
        var doc = ui.Document;
        var globals = new ScriptGlobals { doc = doc, uidoc = ui, app = app };

        using var t = new Transaction(doc, "Claude: script");
        t.Start();
        object? result;
        try
        {
            // Script code is synchronous by contract; RunAsync completes inline here.
            result = script.RunAsync(globals).GetAwaiter().GetResult().ReturnValue;
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            var inner = ex is AggregateException { InnerException: { } ie } ? ie : ex;
            return CallResponse.Fail($"Script failed: {inner.GetType().Name}: {inner.Message}\n{globals.Output}");
        }

        string note;
        if (!t.HasEnded())
        {
            if (commit && doc.IsModifiable)
            {
                note = t.Commit() == TransactionStatus.Committed ? "Changes kept as one undo step." : "Revit rolled the changes back.";
            }
            else
            {
                t.RollBack();
                note = "Read-only run: any changes were rolled back.";
            }
        }
        else
        {
            note = "The script ended the transaction itself.";
        }

        var sb = new StringBuilder();
        if (globals.Output.Length > 0) sb.Append(globals.Output);
        if (result != null) sb.Append(Format(result)).Append('\n');
        sb.Append('[').Append(note).Append(']');
        return CallResponse.Success(TextResult.Cap(sb.ToString(), config.MaxChars));
    }

    /// <summary>Elements become table rows; other sequences become one item per line.</summary>
    public static string Format(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return s;
            case Element e: return Table.RenderRecord(Elements.ToRow(e, ["Id", "Category", "Family", "Type", "Name"]));
            case IEnumerable seq:
                var items = seq.Cast<object?>().Take(Limits.MaxLimit + 1).ToList();
                var more = items.Count > Limits.MaxLimit ? $"\n[first {Limits.MaxLimit} items only]" : "";
                items = items.Take(Limits.MaxLimit).ToList();
                if (items.Count > 0 && items.All(i => i is Element))
                {
                    var rows = items.Cast<Element>().Select(el => Elements.ToRow(el, ["Id", "Category", "Family", "Type", "Name"])).ToList();
                    return Table.Render(rows, Table.Columns(rows, null)).TrimEnd() + more;
                }
                return string.Join("\n", items.Select(i => i is Element el ? $"{el.Id.Value} {el.Name}" : i?.ToString() ?? "null")) + more;
            default: return value.ToString() ?? "";
        }
    }
}
