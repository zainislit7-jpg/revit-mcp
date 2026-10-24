using System.Text;
using AcadMcp.Core;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace AcadMcp.Addin;

sealed partial class AcadHost
{
    public ToolOutput Modify(Args a, McpConfig c)
    {
        var ops = Ops.Parse(a.Str("ops"));
        var dryRun = a.Bool("dry_run", true);
        var doc = ResolveDoc(a);
        if (doc.IsReadOnly && !dryRun) throw new ToolError($"{ShortName(doc)} is open read-only.");
        EnsureIdle(doc);
        var db = doc.Database;

        var log = new StringBuilder();
        // One document lock + one transaction = one undo step named "Claude: modify".
        using var docLock = doc.LockDocument(DocumentLockMode.Write, "Claude: modify", "Claude: modify", false);
        using var tr = db.TransactionManager.StartTransaction();
        var runner = new OpRunner(db, tr, SpaceRecord(db, tr, a.Str("space", "Model")));
        foreach (var op in ops)
        {
            string result;
            try
            {
                result = runner.Run(op);
            }
            catch (ToolError e)
            {
                throw new ToolError($"{e.Message} Nothing was changed.");
            }
            catch (Autodesk.AutoCAD.Runtime.Exception e)
            {
                throw new ToolError($"{op.Label} failed: {e.ErrorStatus}{Hint(e.ErrorStatus)}. Nothing was changed.");
            }
            log.Append('\n').Append(op.Index + 1).Append(' ').Append(op.Kind).Append(": ").Append(result);
        }

        if (dryRun)
        {
            tr.Abort();
            return new ToolOutput($"Dry run OK: {ops.Count} ops valid, nothing changed (handles below are provisional). " +
                                  $"Call again with dry_run=false to apply.{log}");
        }
        tr.Commit();
        return new ToolOutput($"Applied {ops.Count} ops to {ShortName(doc)} as one undo step (Claude: modify).{log}");
    }

    static string Hint(Autodesk.AutoCAD.Runtime.ErrorStatus s) => s switch
    {
        Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer => " (the entity is on a locked layer; unlock it with a layer op first)",
        Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput => " (check the coordinates and sizes)",
        _ => "",
    };

    static BlockTableRecord SpaceRecord(Database db, Transaction tr, string space)
    {
        foreach (var layout in Layouts(db, tr))
            if (string.Equals(layout.LayoutName, space, StringComparison.OrdinalIgnoreCase))
                return (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
        throw new ToolError($"No space '{space}'. Use Model or one of: {string.Join(", ", Layouts(db, tr).Select(l => l.LayoutName))}.");
    }
}
