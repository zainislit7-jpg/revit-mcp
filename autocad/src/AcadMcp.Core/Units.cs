namespace AcadMcp.Core;

/// <summary>INSUNITS values to short names.</summary>
public static class Units
{
    static readonly string[] Names =
    [
        "unitless", "in", "ft", "mi", "mm", "cm", "m", "km", "microin", "mil", "yd", "angstrom", "nm",
        "micron", "dm", "dam", "hm", "Gm", "AU", "ly", "pc", "US survey ft", "US survey in", "US survey yd", "US survey mi",
    ];

    public static string Name(int insunits) => insunits >= 0 && insunits < Names.Length ? Names[insunits] : $"units#{insunits}";
}
