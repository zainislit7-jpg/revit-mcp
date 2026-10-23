using System.Text;

namespace AcadMcp.Core;

/// <summary>Header row plus rows, joined by '|', no padding. Stops accepting rows at a char budget.</summary>
public sealed class PipeTable(IReadOnlyList<string> headers)
{
    readonly StringBuilder _sb = new(string.Join("|", headers));

    public int Rows { get; private set; }
    public int Length => _sb.Length;

    /// <summary>Adds a row unless it would push the table past <paramref name="budget"/> chars.</summary>
    public bool TryAdd(IEnumerable<string?> cells, int budget)
    {
        var line = string.Join("|", cells.Select(c => Fmt.Cell(c)));
        if (_sb.Length + 1 + line.Length > budget && Rows > 0) return false;
        _sb.Append('\n').Append(line);
        Rows++;
        return true;
    }

    public override string ToString() => _sb.ToString();
}
