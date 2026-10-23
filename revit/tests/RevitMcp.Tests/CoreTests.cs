using System.Text.Json.Nodes;
using RevitMcp.Core;

namespace RevitMcp.Tests;

public class FilterTests
{
    private static Row R(params (string K, string? V)[] kv)
    {
        var r = new Row();
        foreach (var (k, v) in kv) r[k] = v;
        return r;
    }

    [Theory]
    [InlineData("Level=Level 1", true)]
    [InlineData("level=LEVEL 1", true)]
    [InlineData("Level!=Level 1", false)]
    [InlineData("Name~door", true)]
    [InlineData("Name!~door", false)]
    [InlineData("Width>900", true)]
    [InlineData("Width>=915", true)]
    [InlineData("Width<900", false)]
    [InlineData("Width=915", true)]
    [InlineData("Mark=", true)]
    [InlineData("Mark!=", false)]
    [InlineData("Level=Level 1; Width>2000", false)]
    public void Matches(string filter, bool expected)
    {
        var row = R(("Level", "Level 1"), ("Name", "Single Door 915"), ("Width", "915 mm"), ("Mark", null));
        Assert.Equal(expected, Filter.Parse(filter).Match(row));
    }

    [Fact]
    public void ValueMayContainOperators() =>
        Assert.Equal("a=b", Filter.Parse("Comments=a=b").Clauses[0].Value);

    [Fact]
    public void ListsFieldsItReads() =>
        Assert.Equal(["Level", "Width"], Filter.Parse("Level=1; Width>3").Fields);

    [Fact]
    public void RejectsClauseWithoutOperator() =>
        Assert.Throws<FormatException>(() => Filter.Parse("Level 1"));

    [Fact]
    public void NonNumericNeverMatchesComparison() =>
        Assert.False(Filter.Parse("Width>3").Match(R(("Width", "n/a"))));
}

public class PagingAndTableTests
{
    private static List<Row> Rows(int n) =>
        Enumerable.Range(1, n).Select(i => new Row { ["Id"] = i.ToString(), ["Name"] = "W" + i, ["Empty"] = null }).ToList();

    [Fact]
    public void FirstPageHasNextCursor()
    {
        var text = TextResult.Page("Walls", Rows(120), 0, 50, null);
        Assert.StartsWith("Walls: 120 (showing 1-50) next:50\nId|Name\n1|W1\n", text);
    }

    [Fact]
    public void LastPageHasNoCursor()
    {
        var text = TextResult.Page("Walls", Rows(120), 100, 50, null);
        Assert.StartsWith("Walls: 120 (showing 101-120)\n", text);
        Assert.DoesNotContain("next:", text);
    }

    [Fact]
    public void EmptyResult() => Assert.Equal("Walls: 0\n", TextResult.Page("Walls", Rows(0), 0, 50, null));

    [Fact]
    public void NullOnlyColumnsAreDropped() =>
        Assert.Equal(["Id", "Name"], Table.Columns(Rows(3), null));

    [Fact]
    public void RequestedFieldsWinOverDefaults() =>
        Assert.Equal(["Name"], Table.Columns(Rows(3), ["Name"], ["Id"]));

    [Fact]
    public void CellsAreSanitised() => Assert.Equal("a/b c", Table.Cell("a|b\r\nc"));

    [Fact]
    public void NumbersAreRounded() => Assert.Equal("1.235", Table.Num(1.23456));

    [Fact]
    public void CapCutsAtLineAndSaysWhy()
    {
        var text = TextResult.Page("Walls", Rows(500), 0, 500, null, maxChars: 1000);
        Assert.True(text.Length <= 1100);
        Assert.Contains("[truncated after", text);
        Assert.Contains("revit_export", text);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("abc", 0)]
    [InlineData("-5", 0)]
    [InlineData("150", 150)]
    public void CursorDecodes(string? cursor, int offset) => Assert.Equal(offset, Paging.Decode(cursor));
}

public class ArgsTests
{
    [Fact]
    public void LenientTypes()
    {
        var a = new Args(JsonNode.Parse("""{"limit":"20","dry_run":"false","ids":"1, 2,x,3","fields":["Id"," Name "]}""")!.AsObject());
        Assert.Equal(20, a.Int("limit", 50));
        Assert.False(a.Bool("dry_run", true));
        Assert.Equal([1L, 2L, 3L], a.Ids());
        Assert.Equal(["Id", "Name"], a.List("fields"));
    }

    [Fact]
    public void LimitIsClamped()
    {
        Assert.Equal(Limits.MaxLimit, new Args(JsonNode.Parse("""{"limit":99999}""")!.AsObject()).Page().Limit);
        Assert.Equal(1, new Args(JsonNode.Parse("""{"limit":0}""")!.AsObject()).Page().Limit);
        Assert.Equal(Limits.DefaultLimit, new Args(null).Page().Limit);
    }

    [Fact]
    public void BlankStringsAreMissing() =>
        Assert.Null(new Args(JsonNode.Parse("""{"category":"  "}""")!.AsObject()).Str("category"));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData(null, "")]
    public void CsvQuote(string? input, string expected) => Assert.Equal(expected, Csv.Quote(input));

    [Fact]
    public void SafeFileName() => Assert.Equal("A101_-_Plan", Csv.SafeFileName("A101 - Plan"));
}

public class ModifyOpTests
{
    [Fact]
    public void ParsesArray()
    {
        var ops = ModifyOp.ParseAll("""[{"op":"SET","ids":[1,2],"param":"Mark","value":"A1"},{"op":"move","ids":[3],"by":[100,0]}]""");
        Assert.Equal("set", ops[0].Op);
        Assert.Equal("A1", ModifyOp.Text(ops[0].Value));
        Assert.Equal([100.0, 0.0], ops[1].By);
    }

    [Fact]
    public void AcceptsSingleObject() =>
        Assert.Single(ModifyOp.ParseAll("""{"op":"delete","ids":[5]}"""));

    [Fact]
    public void NumberValuesKeepTheirText() =>
        Assert.Equal("2400", ModifyOp.Text(ModifyOp.ParseAll("""[{"op":"set","ids":[1],"param":"Height","value":2400}]""")[0].Value));

    [Theory]
    [InlineData("""[{"op":"explode","ids":[1]}]""", "unknown op")]
    [InlineData("""[{"op":"delete","ids":[]}]""", "ids is empty")]
    [InlineData("""[{"op":"set","ids":[1],"param":"Mark"}]""", "needs param and value")]
    [InlineData("""[{"op":"move","ids":[1],"by":[1]}]""", "needs by=")]
    [InlineData("""[{"op":"rotate","ids":[1]}]""", "needs deg")]
    [InlineData("""[{"op":"type","ids":[1]}]""", "needs to")]
    [InlineData("""not json""", "not valid JSON")]
    [InlineData("""[]""", "empty")]
    [InlineData("", "required")]
    public void RejectsBadOps(string json, string message) =>
        Assert.Contains(message, Assert.Throws<ArgumentException>(() => ModifyOp.ParseAll(json)).Message);
}
