using System.Text;
using AcadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace AcadMcp.Addin;

/// <summary>What a run_script snippet can see. Public so Roslyn can bind to it.</summary>
public sealed class ScriptGlobals
{
    public required Document Doc { get; init; }
    public required Database Db { get; init; }
    public required Editor Ed { get; init; }
    public required Transaction Tr { get; init; }

    internal StringBuilder Out { get; } = new();

    public void Print(object? value) => Out.AppendLine(value?.ToString());

    /// <summary>Entities in model space, opened for read.</summary>
    public IEnumerable<Entity> Model => Space("Model");

    /// <summary>Entities in a named layout (or Model), opened for read.</summary>
    public IEnumerable<Entity> Space(string layout) =>
        AcadHost.EntityRows(Db, Tr, Filter.Parse($"Space={layout}")).Select(r => r.Entity);

    /// <summary>Open an object by handle. Use OpenMode.ForWrite to change it.</summary>
    public T Get<T>(string handle, OpenMode mode = OpenMode.ForRead) where T : DBObject =>
        (T)Tr.GetObject(AcadHostIds.IdOf(Db, handle), mode);
}

sealed partial class AcadHost
{
    static readonly Lazy<ScriptOptions> ScriptOpts = new(() => ScriptOptions.Default
        .WithReferences(typeof(Entity).Assembly, typeof(Document).Assembly, typeof(Editor).Assembly,
            typeof(ScriptGlobals).Assembly, typeof(Enumerable).Assembly)
        .WithImports("System", "System.Linq", "System.Text", "System.Collections.Generic",
            "Autodesk.AutoCAD.DatabaseServices", "Autodesk.AutoCAD.Geometry",
            "Autodesk.AutoCAD.ApplicationServices", "Autodesk.AutoCAD.EditorInput"));

    public ToolOutput RunScript(Args a, McpConfig c)
    {
        var code = a.Str("code") ?? throw new ToolError("code is required.");
        var commit = !a.Bool("dry_run", true) && c.AllowWrite;
        var doc = ResolveDoc(a);
        EnsureIdle(doc);

        var script = CSharpScript.Create<object>(code, ScriptOpts.Value, typeof(ScriptGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).Take(5).ToList();
        if (errors.Count > 0)
            throw new ToolError("Script did not compile:\n" + string.Join("\n", errors.Select(d =>
                $"line {d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")));

        using var docLock = doc.LockDocument(DocumentLockMode.Write, "Claude: script", "Claude: script", false);
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var globals = new ScriptGlobals { Doc = doc, Db = doc.Database, Ed = doc.Editor, Tr = tr };
        object? value;
        try
        {
            // Runs synchronously on AutoCAD's thread; scripts must not await.
            value = script.RunAsync(globals).GetAwaiter().GetResult().ReturnValue;
        }
        catch (CompilationErrorException e)
        {
            throw new ToolError("Script did not compile: " + string.Join("; ", e.Diagnostics.Take(5)));
        }
        catch (Exception e) when (e is not ToolError)
        {
            throw new ToolError($"Script threw {e.GetType().Name}: {e.Message}" + (globals.Out.Length > 0 ? $"\nOutput so far:\n{globals.Out}" : ""));
        }

        var note = commit ? "Changes committed as one undo step (Claude: script)."
            : a.Bool("dry_run", true) ? "Rolled back (dry_run)."
            : "Rolled back: writes are off in config.";
        if (commit) tr.Commit(); else tr.Abort();

        var sb = new StringBuilder(globals.Out.ToString().TrimEnd());
        if (value is not null) sb.Append(sb.Length > 0 ? "\n" : "").Append(Format(value));
        if (sb.Length == 0) sb.Append("(no output)");
        return new ToolOutput($"{sb}\n[{note}]");
    }

    static string Format(object value) => value switch
    {
        string s => s,
        double d => Fmt.N(d),
        System.Collections.IDictionary dict => string.Join("\n", dict.Keys.Cast<object>().Select(k => $"{k}: {dict[k]}")),
        System.Collections.IEnumerable list => string.Join("\n", list.Cast<object?>().Select(x => x?.ToString())),
        _ => value.ToString() ?? "",
    };
}
