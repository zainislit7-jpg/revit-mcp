using AcadMcp.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Addin;

/// <summary>Executes validated ops inside the caller's transaction. Returns a short result per op.</summary>
sealed class OpRunner(Database db, Transaction tr, BlockTableRecord space)
{
    static double Rad(double deg) => deg * Math.PI / 180;
    static Point3d P3(Pt p) => new(p.X, p.Y, p.Z);

    public string Run(Op op) => op.Kind switch
    {
        "line" => Add(op, new Line(P3(op.P("from")), P3(op.P("to")))),
        "pline" => Add(op, Polyline(op.Pts("pts"), op.OptBool("closed") ?? false, op.OptNum("width") ?? 0)),
        "rect" => Add(op, Rect(op.P("p1"), op.P("p2"))),
        "circle" => Add(op, new Circle(P3(op.P("center")), Vector3d.ZAxis, op.Num("r"))),
        "arc" => Add(op, new Arc(P3(op.P("center")), op.Num("r"), Rad(op.Num("start")), Rad(op.Num("end")))),
        "text" => Add(op, Text(op)),
        "mtext" => Add(op, MText(op)),
        "dim" => Add(op, Dim(op)),
        "insert" => Insert(op),
        "hatch" => Hatch(op),
        "layer" => Layer(op),
        "set" => Set(op),
        "move" => Transform(op, Matrix3d.Displacement(V(op.P("by")))),
        "rotate" => Transform(op, Matrix3d.Rotation(Rad(op.Num("deg")), Vector3d.ZAxis, P3(op.P("base")))),
        "scale" => Transform(op, Matrix3d.Scaling(op.Num("f"), P3(op.P("base")))),
        "copy" => Copy(op, Matrix3d.Displacement(V(op.P("by")))),
        "mirror" => op.OptBool("keep") ?? true
            ? Copy(op, Mirror(op))
            : Transform(op, Mirror(op)),
        "erase" => Erase(op),
        _ => throw op.Err("not supported"),
    };

    static Vector3d V(Pt p) => new(p.X, p.Y, p.Z);

    static Matrix3d Mirror(Op op) => Matrix3d.Mirroring(new Line3d(P3(op.P("p1")), P3(op.P("p2"))));

    // ---- creation ----

    string Add(Op op, Entity e)
    {
        e.SetDatabaseDefaults(db);
        ApplyCommon(op, e);
        space.AppendEntity(e);
        tr.AddNewlyCreatedDBObject(e, true);
        return "+" + e.Handle;
    }

    void ApplyCommon(Op op, Entity e)
    {
        if (op.OptStr("layer") is { } layer) e.LayerId = LayerId(op, layer);
        if (op.OptStr("color") is { } color) e.Color = ToColor(ColorSpec.Parse(color));
        if (op.OptStr("linetype") is { } lt) e.LinetypeId = LinetypeId(op, lt);
    }

    static Polyline Polyline(IReadOnlyList<Pt> pts, bool closed, double width)
    {
        var pl = new Polyline(pts.Count);
        for (var i = 0; i < pts.Count; i++) pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, width, width);
        pl.Closed = closed;
        pl.Elevation = pts[0].Z;
        return pl;
    }

    static Polyline Rect(Pt a, Pt b) =>
        Polyline([a, new Pt(b.X, a.Y, a.Z), b, new Pt(a.X, b.Y, a.Z)], true, 0);

    DBText Text(Op op)
    {
        var t = new DBText
        {
            Position = P3(op.P("at")),
            TextString = op.Str("text"),
            Height = op.OptNum("h") ?? db.Textsize,
            Rotation = Rad(op.OptNum("rot") ?? 0),
        };
        if (op.OptStr("style") is { } s) t.TextStyleId = TextStyleId(op, s);
        return t;
    }

    MText MText(Op op)
    {
        var m = new MText
        {
            Location = P3(op.P("at")),
            Contents = op.Str("text"),
            TextHeight = op.OptNum("h") ?? db.Textsize,
            Width = op.OptNum("w") ?? 0,
        };
        if (op.OptStr("style") is { } s) m.TextStyleId = TextStyleId(op, s);
        return m;
    }

    Dimension Dim(Op op)
    {
        var style = op.OptStr("style") is { } s ? DimStyleId(op, s) : db.Dimstyle;
        var (p1, p2, at) = (P3(op.P("from")), P3(op.P("to")), P3(op.P("at")));
        return op.OptNum("rot") is { } rot
            ? new RotatedDimension(Rad(rot), p1, p2, at, "", style)
            : new AlignedDimension(p1, p2, at, "", style);
    }

    string Insert(Op op)
    {
        var name = op.Str("block");
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(name))
        {
            var similar = bt.Cast<ObjectId>().Select(id => ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name)
                .Where(n => !n.StartsWith('*') && n.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
            throw op.Err($"no block named '{name}'" + (similar.Count > 0 ? $". Similar: {string.Join(", ", similar)}" : ". See query table=blocks"));
        }
        var def = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
        var scale = op.OptNum("scale") ?? 1;
        var br = new BlockReference(P3(op.P("at")), def.ObjectId)
        {
            ScaleFactors = new Scale3d(scale),
            Rotation = Rad(op.OptNum("rot") ?? 0),
        };
        br.SetDatabaseDefaults(db);
        ApplyCommon(op, br);
        space.AppendEntity(br);
        tr.AddNewlyCreatedDBObject(br, true);

        var values = op.Map("attrs") ?? new Dictionary<string, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in def)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
            var ar = new AttributeReference();
            ar.SetAttributeFromBlock(ad, br.BlockTransform);
            if (values.TryGetValue(ad.Tag, out var v)) { ar.TextString = v; used.Add(ad.Tag); }
            br.AttributeCollection.AppendAttribute(ar);
            tr.AddNewlyCreatedDBObject(ar, true);
        }
        var unknown = values.Keys.Where(k => !used.Contains(k)).ToList();
        if (unknown.Count > 0) throw op.Err($"block '{name}' has no attribute {string.Join(", ", unknown)}");
        return "+" + br.Handle;
    }

    string Hatch(Op op)
    {
        var h = new Hatch();
        h.SetDatabaseDefaults(db);
        ApplyCommon(op, h);
        space.AppendEntity(h);
        tr.AddNewlyCreatedDBObject(h, true);
        h.SetHatchPattern(HatchPatternType.PreDefined, op.OptStr("pattern") ?? "SOLID");
        if (op.OptNum("scale") is { } sc) h.PatternScale = sc;
        if (op.Has("ids"))
        {
            h.Associative = true;
            foreach (var id in Ids(op))
                h.AppendLoop(HatchLoopTypes.Outermost, new ObjectIdCollection([id]));
        }
        else
        {
            var pts = op.Pts("pts").Select(p => new Point2d(p.X, p.Y)).ToList();
            if (pts[0] != pts[^1]) pts.Add(pts[0]);
            h.AppendLoop(HatchLoopTypes.Polyline, new Point2dCollection(pts.ToArray()), new DoubleCollection(new double[pts.Count]));
        }
        h.EvaluateHatch(true);
        return $"+{h.Handle} area {Fmt.N(h.Area)}";
    }

    string Layer(Op op)
    {
        var name = op.Str("name");
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        LayerTableRecord rec;
        string verb;
        if (lt.Has(name))
        {
            rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
            verb = "updated";
        }
        else
        {
            SymbolUtilityServices.ValidateSymbolName(name, false);
            rec = new LayerTableRecord { Name = name };
            lt.UpgradeOpen();
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            verb = "created";
        }
        if (op.OptStr("color") is { } c) rec.Color = ToColor(ColorSpec.Parse(c));
        if (op.OptStr("linetype") is { } l) rec.LinetypeObjectId = LinetypeId(op, l);
        if (op.OptBool("on") is { } on) rec.IsOff = !on;
        if (op.OptBool("locked") is { } locked) rec.IsLocked = locked;
        if (op.OptBool("plot") is { } plot) rec.IsPlottable = plot;
        if (op.OptBool("frozen") is { } frozen)
        {
            if (frozen && rec.ObjectId == db.Clayer) throw op.Err("can't freeze the current layer");
            rec.IsFrozen = frozen;
        }
        if (op.OptBool("current") == true)
        {
            if (rec.IsFrozen) throw op.Err("can't make a frozen layer current");
            db.Clayer = rec.ObjectId;
        }
        return $"{verb} {name}";
    }

    string Set(Op op)
    {
        var n = 0;
        foreach (var id in Ids(op))
        {
            var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
            ApplyCommon(op, e);
            if (op.OptStr("text") is { } text)
            {
                switch (e)
                {
                    case DBText t: t.TextString = text; break;
                    case MText m: m.Contents = text; break;
                    case Dimension d: d.DimensionText = text; break;
                    default: throw op.Err($"{e.Handle} ({e.GetRXClass().DxfName}) has no text");
                }
            }
            if (op.Map("attrs") is { } attrs)
            {
                if (e is not BlockReference br) throw op.Err($"{e.Handle} is not a block reference");
                var left = new HashSet<string>(attrs.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForWrite);
                    if (attrs.TryGetValue(ar.Tag, out var v)) { ar.TextString = v; left.Remove(ar.Tag); }
                }
                if (left.Count > 0) throw op.Err($"{e.Handle} has no attribute {string.Join(", ", left)}");
            }
            n++;
        }
        return $"changed {n}";
    }

    string Transform(Op op, Matrix3d m)
    {
        var ids = Ids(op);
        foreach (var id in ids) ((Entity)tr.GetObject(id, OpenMode.ForWrite)).TransformBy(m);
        return $"{ids.Count} transformed";
    }

    string Copy(Op op, Matrix3d m)
    {
        var created = new List<string>();
        // DeepClone keeps attributes, xdata and reactors that a plain Clone would lose.
        foreach (var group in Ids(op).GroupBy(id => tr.GetObject(id, OpenMode.ForRead).OwnerId))
        {
            var map = new IdMapping();
            db.DeepCloneObjects(new ObjectIdCollection(group.ToArray()), group.Key, map, false);
            foreach (var src in group)
            {
                var e = (Entity)tr.GetObject(map[src].Value, OpenMode.ForWrite);
                e.TransformBy(m);
                created.Add("+" + e.Handle);
            }
        }
        return string.Join(" ", created);
    }

    string Erase(Op op)
    {
        var ids = Ids(op);
        foreach (var id in ids) tr.GetObject(id, OpenMode.ForWrite).Erase();
        return $"{ids.Count} erased";
    }

    // ---- lookups ----

    List<ObjectId> Ids(Op op) => op.Ids().Select(h =>
    {
        ObjectId id;
        try { id = AcadHostIds.IdOf(db, h); }
        catch (ToolError e) { throw op.Err(e.Message.TrimEnd('.')); }
        if (!id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Entity)))) throw op.Err($"{h} is not an entity");
        return id;
    }).ToList();

    ObjectId LayerId(Op op, string name)
    {
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        return lt.Has(name) ? lt[name] : throw op.Err($"no layer '{name}'; add {{\"op\":\"layer\",\"name\":\"{name}\"}} before it");
    }

    ObjectId LinetypeId(Op op, string name)
    {
        var t = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (t.Has(name)) return t[name];
        try { db.LoadLineTypeFile(name, "acad.lin"); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { throw op.Err($"linetype '{name}' not loaded and not in acad.lin"); }
        return t[name];
    }

    ObjectId TextStyleId(Op op, string name)
    {
        var t = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        return t.Has(name) ? t[name] : throw op.Err($"no text style '{name}'");
    }

    ObjectId DimStyleId(Op op, string name)
    {
        var t = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        return t.Has(name) ? t[name] : throw op.Err($"no dim style '{name}'");
    }

    static Color ToColor(ColorSpec c) => c.Kind switch
    {
        "bylayer" => Color.FromColorIndex(ColorMethod.ByLayer, 256),
        "byblock" => Color.FromColorIndex(ColorMethod.ByBlock, 0),
        "index" => Color.FromColorIndex(ColorMethod.ByAci, c.Index),
        _ => Color.FromRgb(c.R, c.G, c.B),
    };
}
