using System.Text.Json;
using Autodesk.Revit.DB;

namespace RevitMcp.Addin;

/// <summary>Turns modify lengths into Revit's internal feet.</summary>
public static class Lengths
{
    public static XYZ Vector(Document doc, JsonElement[] by, string units) =>
        new(Feet(doc, by[0], units), Feet(doc, by[1], units), by.Length > 2 ? Feet(doc, by[2], units) : 0);

    /// <summary>
    /// A number is read in <paramref name="units"/> ("project" = the model's length unit).
    /// Text goes through Revit's own parser, so 2'-6", 2' 6 1/2", 30" and 1500 mm all work.
    /// </summary>
    public static double Feet(Document doc, JsonElement v, string units)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var text = v.GetString()!.Trim();
            if (UnitFormatUtils.TryParse(doc.GetUnits(), SpecTypeId.Length, text, out var parsed)) return parsed;
            throw new ArgumentException($"Can't read '{text}' as a length. Try 2'-6\", 30\" or 1500 mm.");
        }
        var n = v.GetDouble();
        var unit = units switch
        {
            "ft" => UnitTypeId.Feet,
            "in" => UnitTypeId.Inches,
            "mm" => UnitTypeId.Millimeters,
            "cm" => UnitTypeId.Centimeters,
            "m" => UnitTypeId.Meters,
            _ => NumericUnit(doc),
        };
        return UnitUtils.ConvertToInternalUnits(n, unit);
    }

    /// <summary>
    /// The unit a bare number means in "project" mode. Feet-and-inches display formats map
    /// to decimal feet, since a number like 2.5 can only sensibly mean 2.5 ft there.
    /// </summary>
    public static ForgeTypeId NumericUnit(Document doc)
    {
        var u = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
        if (u == UnitTypeId.FeetFractionalInches) return UnitTypeId.Feet;
        if (u == UnitTypeId.FractionalInches) return UnitTypeId.Inches;
        if (u == UnitTypeId.MetersCentimeters) return UnitTypeId.Meters;
        return u;
    }
}
