using AcadMcp.Core;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Addin;

/// <summary>A row whose values are computed on first use.</summary>
sealed class FuncRow(IReadOnlyList<(string Field, Func<string?> Value)> fields) : IRow
{
    public string? Get(string field)
    {
        foreach (var (f, v) in fields)
            if (string.Equals(f, field, StringComparison.OrdinalIgnoreCase)) return v();
        return null;
    }

    public IEnumerable<string> Fields => fields.Select(f => f.Field);
}

/// <summary>Lazy view of one entity. Only the fields asked for are ever computed.</summary>
sealed class EntityRow(Entity e, string space, Transaction tr) : IRow
{
    public static readonly string[] All =
    [
        "Handle", "Type", "Layer", "Color", "Linetype", "Lineweight", "Space", "Block", "Text", "Length", "Area", "Closed",
        "Verts", "Start", "End", "Center", "Radius", "Pos", "Rot", "Scale", "Height", "Style", "Measurement", "Pattern",
        "Bounds", "Attrs",
    ];

    public static readonly string[] Defaults = ["Handle", "Type", "Layer"];

    public Entity Entity => e;
    public IEnumerable<string> Fields => All;

    public string? Get(string field)
    {
        try
        {
            return field.ToLowerInvariant() switch
            {
                "handle" => e.Handle.ToString(),
                "type" => e.GetRXClass().DxfName,
                "layer" => e.Layer,
                "color" => ColorText(e.Color),
                "linetype" => e.Linetype,
                "lineweight" => Lw(e.LineWeight),
                "space" => space,
                "block" => e is BlockReference br ? BlockName(br, tr) : null,
                "text" => TextOf(e),
                "length" => e is Curve c and not Xline and not Ray ? Fmt.N(c.GetDistanceAtParameter(c.EndParam) - c.GetDistanceAtParameter(c.StartParam)) : null,
                "area" => e switch
                {
                    Hatch h => Fmt.N(h.Area),
                    Region r => Fmt.N(r.Area),
                    Curve c when c.Closed || c is Circle or Ellipse => Fmt.N(c.Area),
                    _ => null,
                },
                "closed" => e is Curve c ? Fmt.Bool(c.Closed) : null,
                "verts" => e is Polyline pl ? pl.NumberOfVertices.ToString() : null,
                "start" => e is Curve c and not Circle and not Xline and not Ray ? P(c.StartPoint) : null,
                "end" => e is Curve c and not Circle and not Xline and not Ray ? P(c.EndPoint) : null,
                "center" => e switch { Circle c => P(c.Center), Arc a => P(a.Center), Ellipse el => P(el.Center), _ => null },
                "radius" => e switch { Circle c => Fmt.N(c.Radius), Arc a => Fmt.N(a.Radius), _ => null },
                "pos" => e switch
                {
                    BlockReference b => P(b.Position),
                    DBText t => P(t.Position),
                    MText m => P(m.Location),
                    DBPoint p => P(p.Position),
                    _ => null,
                },
                "rot" => e switch
                {
                    BlockReference b => Deg(b.Rotation),
                    DBText t => Deg(t.Rotation),
                    MText m => Deg(m.Rotation),
                    _ => null,
                },
                "scale" => e is BlockReference b2 ? ScaleText(b2.ScaleFactors) : null,
                "height" => e switch { DBText t => Fmt.N(t.Height), MText m => Fmt.N(m.TextHeight), Dimension d => Fmt.N(d.Dimtxt), _ => null },
                "style" => e switch { DBText t => t.TextStyleName, MText m => m.TextStyleName, Dimension d => d.DimensionStyleName, _ => null },
                "measurement" => e is Dimension d2 ? Fmt.N(d2.Measurement) : null,
                "pattern" => e is Hatch h2 ? h2.PatternName : null,
                "bounds" => e.Bounds is { } x ? $"{Fmt.Pt(x.MinPoint.X, x.MinPoint.Y)};{Fmt.Pt(x.MaxPoint.X, x.MaxPoint.Y)}" : null,
                "attrs" => e is BlockReference b3 ? string.Join("; ", Attributes(b3, tr).Select(kv => $"{kv.Key}={kv.Value}")) : null,
                _ => null,
            };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            return null; // some entities don't support a property (e.g. length of a spline with no params)
        }
    }

    public static string P(Point3d p) => Fmt.Pt(p.X, p.Y, p.Z);
    static string Deg(double rad) => Fmt.N(rad * 180 / Math.PI, 2);

    static string ScaleText(Scale3d s) =>
        Math.Abs(s.X - s.Y) < 1e-9 && Math.Abs(s.X - s.Z) < 1e-9 ? Fmt.N(s.X) : $"{Fmt.N(s.X)},{Fmt.N(s.Y)},{Fmt.N(s.Z)}";

    public static string ColorText(Color c) =>
        c.IsByLayer ? "ByLayer"
        : c.IsByBlock ? "ByBlock"
        : c.ColorMethod == ColorMethod.ByAci ? c.ColorIndex.ToString()
        : $"{c.Red},{c.Green},{c.Blue}";

    public static string Lw(LineWeight lw) => (int)lw switch
    {
        -1 => "ByLayer",
        -2 => "ByBlock",
        -3 => "Default",
        var n => Fmt.N(n / 100.0, 2),
    };

    static string? TextOf(Entity e) => e switch
    {
        DBText t => t.TextString,
        MText m => m.Text,
        Dimension d => string.IsNullOrEmpty(d.DimensionText) ? null : d.DimensionText,
        MLeader ml when ml.ContentType == ContentType.MTextContent => ml.MText?.Text,
        _ => null,
    };

    public static string BlockName(BlockReference br, Transaction tr)
    {
        if (!br.IsDynamicBlock) return br.Name;
        var btr = (BlockTableRecord)tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead);
        return btr.Name;
    }

    public static List<KeyValuePair<string, string>> Attributes(BlockReference br, Transaction tr)
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (ObjectId id in br.AttributeCollection)
            if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar)
                list.Add(new(ar.Tag, ar.TextString));
        return list;
    }
}

/// <summary>What a query/export can read: entities or one of the drawing's tables.</summary>
sealed record Source(
    string Title,
    IReadOnlyList<string> Known,
    IReadOnlyList<string> Defaults,
    string IdField,
    string TypeField,
    string DefaultGroup,
    Func<Filter, IEnumerable<IRow>> Rows,
    Func<IReadOnlyList<string>, IEnumerable<IRow>> ByIds);
