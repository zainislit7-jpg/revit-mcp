using System.Text;
using Autodesk.Revit.DB;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

/// <summary>Applies all ops in one transaction, so the whole call is one Ctrl+Z.</summary>
public static class ModifyHandler
{
    public static List<ModifyOp> Parse(string? json) => ModifyOp.ParseAll(json);

    public static CallResponse Run(Document doc, List<ModifyOp> ops, bool dryRun, string units)
    {
        if (doc.IsReadOnly) return CallResponse.Fail("Error: the model is read-only.");
        var log = new StringBuilder();
        var failures = new FailureCollector();

        using var t = new Transaction(doc, $"Claude: {Describe(ops)}");
        var fho = t.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(failures);
        fho.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(fho);
        t.Start();

        var errors = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            try
            {
                log.Append($"{i}: ").Append(Apply(doc, ops[i], units)).Append('\n');
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                        or Autodesk.Revit.Exceptions.ApplicationException)
            {
                errors++;
                log.Append($"{i}: FAILED {ops[i].Op}: {ex.Message}\n");
            }
        }

        string outcome;
        if (dryRun || errors > 0)
        {
            t.RollBack();
            outcome = dryRun
                ? $"Dry run: {ops.Count} op(s), {errors} failed. Nothing was changed. Run again with dry_run=false to apply."
                : $"{errors} op(s) failed, so nothing was changed.";
        }
        else
        {
            var status = t.Commit();
            outcome = status == TransactionStatus.Committed
                ? $"Applied {ops.Count} op(s) as one undo step \"{t.GetName()}\"."
                : $"Revit rolled the change back ({status}). Nothing was changed.";
        }

        var sb = new StringBuilder(outcome).Append('\n').Append(log);
        if (failures.Messages.Count > 0) sb.Append("Revit messages:\n").AppendJoin('\n', failures.Messages.Distinct().Take(20)).Append('\n');
        return CallResponse.Success(TextResult.Cap(sb.ToString()));
    }

    private static string Describe(List<ModifyOp> ops) =>
        ops.Count switch
        {
            1 when ops[0].IsCreate => $"create {ops[0].Op}",
            1 => $"{ops[0].Op} {ops[0].Ids.Count} element(s)",
            _ => $"{ops.Count} changes",
        };

    private static string Apply(Document doc, ModifyOp op, string units)
    {
        if (op.IsCreate) return CreateOps.Apply(doc, op, units);

        var ids = op.Ids.Select(i => new ElementId(i)).ToList();
        var missing = ids.Where(id => doc.GetElement(id) == null).Select(id => id.Value).ToList();
        if (missing.Count > 0) throw new ArgumentException("not found: " + string.Join(",", missing.Take(10)));

        switch (op.Op)
        {
            case "set":
                var text = ModifyOp.Text(op.Value);
                foreach (var id in ids) SetParam(doc.GetElement(id), op.Param!, text);
                return $"set {op.Param}={text} on {ids.Count}";

            case "move":
                var move = Lengths.Vector(doc, op.By!, units);
                ElementTransformUtils.MoveElements(doc, ids, move);
                return $"moved {ids.Count} by {Elements.Point(doc, move)}";

            case "copy":
                var copies = ElementTransformUtils.CopyElements(doc, ids, Lengths.Vector(doc, op.By!, units));
                return $"copied {ids.Count}, new ids {string.Join(",", copies.Select(c => c.Value).Take(50))}";

            case "rotate":
                var angle = op.Deg!.Value * Math.PI / 180;
                foreach (var id in ids)
                {
                    var c = Center(doc.GetElement(id)) ?? throw new ArgumentException($"{id.Value} has no location to rotate about.");
                    ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(c, c + XYZ.BasisZ), angle);
                }
                return $"rotated {ids.Count} by {op.Deg} deg";

            case "type":
                var type = ResolveType(doc, doc.GetElement(ids[0]), ModifyOp.Text(op.To)!);
                foreach (var id in ids) doc.GetElement(id).ChangeTypeId(type.Id);
                return $"changed {ids.Count} to type {type.Name} ({type.Id.Value})";

            case "delete":
                var deleted = doc.Delete(ids);
                return $"deleted {ids.Count} (with dependents: {deleted.Count})";

            case "pin" or "unpin":
                foreach (var id in ids) doc.GetElement(id).Pinned = op.Op == "pin";
                return $"{op.Op}ned {ids.Count}";

            default:
                throw new ArgumentException("unknown op");
        }
    }

    private static XYZ? Center(Element e) => e.Location switch
    {
        LocationPoint lp => lp.Point,
        LocationCurve lc => lc.Curve.Evaluate(0.5, true),
        _ => e.get_BoundingBox(null) is { } bb ? (bb.Min + bb.Max) / 2 : null,
    };

    private static void SetParam(Element e, string name, string? value)
    {
        var p = e.LookupParameter(name)
                ?? throw new ArgumentException($"{e.Id.Value} has no instance parameter '{name}'. Type parameters need the type's id.");
        if (p.IsReadOnly) throw new ArgumentException($"'{name}' is read-only on {e.Id.Value}.");
        var ok = p.StorageType switch
        {
            StorageType.String => p.Set(value ?? ""),
            StorageType.Integer when int.TryParse(value, out var i) => p.Set(i),
            StorageType.Double or StorageType.Integer => p.SetValueString(value ?? ""),
            StorageType.ElementId when long.TryParse(value, out var id) => p.Set(new ElementId(id)),
            _ => false,
        };
        if (!ok) throw new ArgumentException($"Revit did not accept '{value}' for '{name}' on {e.Id.Value}.");
    }

    private static ElementType ResolveType(Document doc, Element sample, string idOrName)
    {
        if (long.TryParse(idOrName, out var id) && doc.GetElement(new ElementId(id)) is ElementType byId) return byId;
        var valid = sample.GetValidTypes().Select(doc.GetElement).OfType<ElementType>().ToList();
        return valid.FirstOrDefault(t => string.Equals(t.Name, idOrName, StringComparison.OrdinalIgnoreCase))
               ?? valid.FirstOrDefault(t => string.Equals($"{t.FamilyName}: {t.Name}", idOrName, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No valid type '{idOrName}'. Options: " + string.Join(", ", valid.Select(t => t.Name).Take(15)));
    }

    /// <summary>Swallows warnings (reporting them) and turns errors into a rollback instead of a dialog.</summary>
    private sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<string> Messages { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            var hasError = false;
            foreach (var m in fa.GetFailureMessages())
            {
                Messages.Add((m.GetSeverity() == FailureSeverity.Warning ? "warning: " : "error: ") + m.GetDescriptionText());
                if (m.GetSeverity() == FailureSeverity.Warning) fa.DeleteWarning(m);
                else hasError = true;
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
