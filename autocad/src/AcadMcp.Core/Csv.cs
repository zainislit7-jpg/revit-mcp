namespace AcadMcp.Core;

public static class Csv
{
    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static void WriteRow(TextWriter w, IEnumerable<string?> cells) => w.WriteLine(string.Join(",", cells.Select(Escape)));
}
