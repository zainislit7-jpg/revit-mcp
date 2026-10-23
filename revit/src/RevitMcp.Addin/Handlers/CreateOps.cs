using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using RevitMcp.Core;

namespace RevitMcp.Addin.Handlers;

/// <summary>
/// The create half of revit_modify: walls, floors, doors, windows, rooms and levels. These cover
/// ordinary layout work, so it doesn't need the run_script escape hatch.
/// </summary>
public static class CreateOps
{
    private const double DefaultWallHeightFt = 10;
    private const double MaxHostDistanceFt = 3;

    public static string Apply(Document doc, ModifyOp op, string units) => op.Op switch
    {
        "wall" => Walls(doc, op, units),
        "floor" => Floor(doc, op, units),
        "door" => Opening(doc, op, units, BuiltInCategory.OST_Doors),
        "window" => Opening(doc, op, units, BuiltInCategory.OST_Windows),
        "room" => Room(doc, op, units),
        "level" => NewLevel(doc, op, units),
        _ => throw new ArgumentException("unknown create op"),
    };

    private static string Walls(Document doc, ModifyOp op, string units)
    {
        var level = ResolveLevel(doc, op.Level);
        var type = ResolveType<WallType>(doc, op.TypeName, doc.GetDefaultElementTypeId(ElementTypeGroup.WallType), "wall");
        var height = op.Height is { } h ? Lengths.Feet(doc, h, units) : DefaultWallHeightFt;
        var pts = Points(doc, op.Path!, units, level.Elevation);
        if (op.Closed && pts.Count >= 3) pts.Add(pts[0]);

        var ids = new List<long>();
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            if (pts[i].DistanceTo(pts[i + 1]) < doc.Application.ShortCurveTolerance) continue;
            var wall = Wall.Create(doc, Line.CreateBound(pts[i], pts[i + 1]), type.Id, level.Id, height, 0, false, false);
            ids.Add(wall.Id.Value);
        }
        return $"created {ids.Count} {type.Name} wall(s) on {level.Name}, {Elements.Length(doc, height)} high, ids {string.Join(",", ids)}";
    }

    private static string Floor(Document doc, ModifyOp op, string units)
    {
        var level = ResolveLevel(doc, op.Level);
        var type = ResolveType<FloorType>(doc, op.TypeName, doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType), "floor");
        var pts = Points(doc, op.Path!, units, level.Elevation);
        var loop = new CurveLoop();
        for (var i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        var floor = Autodesk.Revit.DB.Floor.Create(doc, [loop], type.Id, level.Id);
        return $"created {type.Name} floor on {level.Name}, id {floor.Id.Value}";
    }

    /// <summary>Doors and windows: hosted on the given wall, or the nearest wall on the level, at the point projected onto it.</summary>
    private static string Opening(Document doc, ModifyOp op, string units, BuiltInCategory bic)
    {
        var level = ResolveLevel(doc, op.Level);
        var symbol = ResolveSymbol(doc, op.TypeName, bic);
        // Walls made earlier in the same call need their geometry before they can host.
        doc.Regenerate();
        var at = Point(doc, op.At!, units, level.Elevation);

        var (wall, onWall) = Host(doc, op.Wall, level, at);
        var inst = doc.Create.NewFamilyInstance(onWall, symbol, wall, level, StructuralType.NonStructural);
        if (op.Sill is { } sill)
            inst.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(Lengths.Feet(doc, sill, units));
        if (!string.IsNullOrWhiteSpace(op.Number))
            inst.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(op.Number);
        return $"placed {symbol.FamilyName}: {symbol.Name} in wall {wall.Id.Value} at {Elements.Point(doc, onWall)}, id {inst.Id.Value}";
    }

    private static string Room(Document doc, ModifyOp op, string units)
    {
        var level = ResolveLevel(doc, op.Level);
        // Rooms find their boundaries from walls, so walls created earlier in this call must exist first.
        doc.Regenerate();
        var at = Point(doc, op.At!, units, level.Elevation);
        var room = doc.Create.NewRoom(level, new UV(at.X, at.Y));
        if (!string.IsNullOrWhiteSpace(op.Name)) room.Name = op.Name;
        if (!string.IsNullOrWhiteSpace(op.Number)) room.Number = op.Number;
        doc.Regenerate();

        var tagged = "";
        if (doc.ActiveView is ViewPlan plan && plan.GenLevel?.Id == level.Id)
        {
            doc.Create.NewRoomTag(new LinkElementId(room.Id), new UV(at.X, at.Y), plan.Id);
            tagged = ", tagged";
        }
        var area = room.Area > 0
            ? room.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsValueString()
            : null;
        return area != null
            ? $"room {room.Number} {room.Name} on {level.Name}, area {area}{tagged}, id {room.Id.Value}"
            : $"room {room.Number} {room.Name} placed but NOT enclosed (area 0): check walls around {Elements.Point(doc, at)}, id {room.Id.Value}";
    }

    private static string NewLevel(Document doc, ModifyOp op, string units)
    {
        var level = Level.Create(doc, Lengths.Feet(doc, op.Elevation!.Value, units));
        if (!string.IsNullOrWhiteSpace(op.Name)) level.Name = op.Name;
        var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
        var plan = planType != null ? ViewPlan.Create(doc, planType.Id, level.Id) : null;
        return $"level {level.Name} at {Elements.Length(doc, level.Elevation)}, id {level.Id.Value}"
               + (plan != null ? $", floor plan id {plan.Id.Value}" : "");
    }

    private static List<XYZ> Points(Document doc, JsonElement[][] path, string units, double z) =>
        path.Select(p => Point(doc, p, units, z)).ToList();

    private static XYZ Point(Document doc, JsonElement[] p, string units, double z) =>
        new(Lengths.Feet(doc, p[0], units), Lengths.Feet(doc, p[1], units), p.Length > 2 ? z + Lengths.Feet(doc, p[2], units) : z);

    /// <summary>A level by id or name; default is the active plan's level, else the lowest.</summary>
    public static Level ResolveLevel(Document doc, string? idOrName)
    {
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        if (levels.Count == 0) throw new InvalidOperationException("The model has no levels. Create one with op level first.");
        if (string.IsNullOrWhiteSpace(idOrName))
            return (doc.ActiveView as ViewPlan)?.GenLevel ?? levels[0];
        if (long.TryParse(idOrName, out var id) && doc.GetElement(new ElementId(id)) is Level byId) return byId;
        return levels.FirstOrDefault(l => string.Equals(l.Name, idOrName, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException($"No level '{idOrName}'. Levels: {string.Join(", ", levels.Select(l => l.Name))}");
    }

    private static T ResolveType<T>(Document doc, string? name, ElementId fallback, string what) where T : ElementType
    {
        var types = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().ToList();
        if (string.IsNullOrWhiteSpace(name))
            return doc.GetElement(fallback) as T ?? types.FirstOrDefault()
                   ?? throw new InvalidOperationException($"The model has no {what} types.");
        return Match(types, name)
               ?? throw new ArgumentException($"No {what} type '{name}'. Options: {string.Join(", ", types.Select(t => t.Name).Take(15))}");
    }

    private static FamilySymbol ResolveSymbol(Document doc, string? name, BuiltInCategory bic)
    {
        var what = bic == BuiltInCategory.OST_Doors ? "door" : "window";
        var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(bic).Cast<FamilySymbol>().ToList();
        if (symbols.Count == 0)
            throw new InvalidOperationException($"No {what} families are loaded. Load one in Revit (Insert > Load Family) first.");
        var symbol = string.IsNullOrWhiteSpace(name)
            ? doc.GetElement(doc.GetDefaultFamilyTypeId(new ElementId(bic))) as FamilySymbol ?? symbols[0]
            : Match(symbols, name) ?? throw new ArgumentException(
                $"No {what} type '{name}'. Options: {string.Join(", ", symbols.Select(s => $"{s.FamilyName}: {s.Name}").Take(15))}");
        if (!symbol.IsActive) symbol.Activate();
        return symbol;
    }

    /// <summary>Exact "Name" or "Family: Name", then a contains match, so "36\" x 84\"" finds "Single-Flush: 36\" x 84\"".</summary>
    private static T? Match<T>(List<T> types, string name) where T : ElementType =>
        types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals($"{t.FamilyName}: {t.Name}", name, StringComparison.OrdinalIgnoreCase))
        ?? types.FirstOrDefault(t => $"{t.FamilyName}: {t.Name}".Contains(name, StringComparison.OrdinalIgnoreCase));

    private static (Wall, XYZ) Host(Document doc, long? wallId, Level level, XYZ at)
    {
        IEnumerable<Wall> candidates = wallId is { } id
            ? [doc.GetElement(new ElementId(id)) as Wall ?? throw new ArgumentException($"{id} is not a wall.")]
            : new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.LevelId == level.Id);

        Wall? best = null;
        XYZ? bestPoint = null;
        var bestDist = double.MaxValue;
        foreach (var w in candidates)
        {
            if (w.Location is not LocationCurve lc) continue;
            var hit = lc.Curve.Project(at);
            if (hit == null || hit.Distance >= bestDist) continue;
            (best, bestPoint, bestDist) = (w, hit.XYZPoint, hit.Distance);
        }
        if (best == null || (wallId == null && bestDist > MaxHostDistanceFt))
            throw new ArgumentException($"No wall on {level.Name} within 3' of {Elements.Point(doc, at)}. Give wall=<id> or a point on a wall.");
        return (best, new XYZ(bestPoint!.X, bestPoint.Y, level.Elevation));
    }
}
