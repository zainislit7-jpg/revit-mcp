using System.Globalization;
using System.Text;

namespace AcadMcp.Core;

/// <summary>Compact, culture-invariant formatting for tool output.</summary>
public static class Fmt
{
    public static string N(double v, int decimals = 3)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "";
        var r = Math.Round(v, decimals);
        if (r == 0) r = 0; // no "-0"
        return r.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string Pt(double x, double y, double z = 0) =>
        Math.Abs(z) < 1e-9 ? $"{N(x)},{N(y)}" : $"{N(x)},{N(y)},{N(z)}";

    public static string Count(int n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Bool(bool b) => b ? "Y" : "N";

    /// <summary>Make a value safe for a pipe-table cell: one line, no pipes, bounded length.</summary>
    public static string Cell(string? s, int max = 120)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        foreach (var c in s)
        {
            sb.Append(c switch { '|' => '/', '\r' or '\n' or '\t' => ' ', _ => c });
            if (sb.Length > max) { sb.Length = max; sb.Append('…'); break; }
        }
        return sb.ToString().Trim();
    }
}
