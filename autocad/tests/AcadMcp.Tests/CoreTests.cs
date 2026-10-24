using System.Text.Json;
using AcadMcp.Core;

namespace AcadMcp.Tests;

public class FilterTests
{
    static string? Get(string k) => k.ToLowerInvariant() switch { "layer" => "A-WALL-EXT", "type" => "LWPOLYLINE", _ => null };

    [Theory]
    [InlineData("Layer=A-WALL*", true)]
    [InlineData("layer=a-wall*", true)]
    [InlineData("Layer=A-DOOR,A-WALL-EXT", true)]
    [InlineData("Layer=A-DOOR", false)]
    [InlineData("Layer!=A-WALL*", false)]
    [InlineData("Layer~wall", true)]
    [InlineData("Layer=A-WALL*; Type=LINE", false)]
    [InlineData("Layer=A-WALL*; Type=*POLYLINE", true)]
    [InlineData("Layer=A-WALL-EX?", true)]
    [InlineData("Text=", true)]
    public void Matches(string filter, bool expected) => Assert.Equal(expected, Filter.Parse(filter).Matches(Get));

    [Fact]
    public void UnknownKeyIsRejectedWithKeyList()
    {
        var e = Assert.Throws<ToolError>(() => Filter.Parse("Lyer=0", ["Layer", "Type"]));
        Assert.Contains("Layer, Type", e.Message);
    }

    [Fact]
    public void KeysAreCanonicalised() => Assert.Equal("Space", Filter.Parse("space=Layout1", ["Space"]).Clauses[0].Key);

    [Fact]
    public void ValuesForReturnsPositiveClauseOnly()
    {
        Assert.Equal(["Layout1", "Layout2"], Filter.Parse("Space=Layout1,Layout2").ValuesFor("space"));
        Assert.Null(Filter.Parse("Space!=Model").ValuesFor("Space"));
        Assert.True(Filter.Parse("Space!=Model").Mentions("Space"));
    }

    [Fact]
    public void BadClauseExplainsSyntax() => Assert.Contains("Key=Value", Assert.Throws<ToolError>(() => Filter.Parse("Layer")).Message);
}

public class QueryTests
{
    static readonly string[] Known = ["Handle", "Type", "Layer", "Text"];

    static List<IRow> Rows(int n) => Enumerable.Range(1, n).Select(i => (IRow)new DictRow(new Dictionary<string, string?>
    {
        ["Handle"] = i.ToString("X"),
        ["Type"] = i % 3 == 0 ? "CIRCLE" : "LINE",
        ["Layer"] = i % 2 == 0 ? "A-WALL" : "A-DOOR",
        ["Text"] = null,
    })).ToList();

    [Fact]
    public void ListPagesWithNextCursor()
    {
        var text = Query.List("Entities", Rows(120), Filter.None, ["Handle", "Type"], 50, 0, 8000);
        var lines = text.Split('\n');
        Assert.Equal("Entities: 120 (showing 1-50) next:50", lines[0]);
        Assert.Equal("Handle|Type", lines[1]);
        Assert.Equal(52, lines.Length);
    }

    [Fact]
    public void LastPageHasNoNext()
    {
        var text = Query.List("Entities", Rows(120), Filter.None, ["Handle"], 50, Query.Offset("next:100"), 8000);
        Assert.StartsWith("Entities: 120 (showing 101-120)\n", text);
    }

    [Fact]
    public void FilterAppliesBeforePaging()
    {
        var text = Query.List("Entities", Rows(120), Filter.Parse("Type=CIRCLE; Layer=A-WALL"), ["Handle"], 50, 0, 8000);
        Assert.StartsWith("Entities: 20 (showing 1-20)", text);
    }

    [Fact]
    public void CharBudgetShrinksPageButKeepsCursorExact()
    {
        var text = Query.List("Entities", Rows(500), Filter.None, ["Handle", "Type", "Layer"], 500, 0, 1200);
        Assert.True(text.Length <= 1200, $"length {text.Length}");
        var first = text.Split('\n')[0];
        var shown = int.Parse(first.Split("showing 1-")[1].Split(')')[0]);
        Assert.Contains($"next:{shown}", first);
        Assert.Equal(shown + 2, text.Split('\n').Length);
    }

    [Fact]
    public void EmptyResultSuggestsSummary() =>
        Assert.Contains("mode=summary", Query.List("Entities", Rows(5), Filter.Parse("Layer=NOPE"), ["Handle"], 50, 0, 8000));

    [Fact]
    public void SummaryCountsAndTruncatesGroups()
    {
        var text = Query.Summary("Entities", Rows(90), Filter.None, "Type", 1, "units mm");
        Assert.Equal("Entities: 90 in 2 Type groups · units mm\nType|Count\nLINE|60\n(+1 more groups, 30 items; raise limit or filter)", text);
    }

    [Fact]
    public void GetDropsEmptyValues()
    {
        var text = Query.Get(Rows(2), null, 8000, "Handle", "Type");
        Assert.Equal("1 LINE Layer=A-DOOR\n2 LINE Layer=A-WALL", text);
    }

    [Fact]
    public void FieldsAreValidatedAndStarExpands()
    {
        Assert.Equal(["Handle", "Layer"], Query.Fields("handle, LAYER", ["Handle"], Known));
        Assert.Equal(Known, Query.Fields("*", ["Handle"], Known));
        Assert.Contains("Fields: Handle, Type", Assert.Throws<ToolError>(() => Query.Fields("Colour", ["Handle"], Known)).Message);
    }

    [Fact]
    public void CellsAreSingleLineWithoutPipes()
    {
        var row = new DictRow(new Dictionary<string, string?> { ["Text"] = "KITCHEN|DINING\nLevel 2" });
        var text = Query.List("Entities", [row], Filter.None, ["Text"], 50, 0, 8000);
        Assert.EndsWith("Text\nKITCHEN/DINING Level 2", text);
    }
}

public class OpsTests
{
    [Fact]
    public void ParsesTypicalBatch()
    {
        var ops = Ops.Parse("""
            [{"op":"layer","name":"A-WALL","color":"1"},
             {"op":"line","from":[0,0],"to":[5000,0],"layer":"A-WALL"},
             {"op":"pline","pts":[[0,0],[10,0],[10,10]],"closed":true},
             {"op":"insert","block":"DOOR","at":"100,200","attrs":{"MARK":"D01"}},
             {"op":"move","ids":"2A3F,2A40","by":[0,100]}]
            """);
        Assert.Equal(["layer", "line", "pline", "insert", "move"], ops.Select(o => o.Kind));
        Assert.Equal(new Pt(100, 200), ops[3].P("at"));
        Assert.Equal("D01", ops[3].Map("attrs")!["mark"]);
        Assert.Equal(["2A3F", "2A40"], ops[4].Ids());
    }

    [Fact]
    public void SingleObjectIsAccepted() => Assert.Single(Ops.Parse("""{"op":"circle","center":[0,0],"r":5}"""));

    [Theory]
    [InlineData("""[{"op":"spline"}]""", "unknown op 'spline'")]
    [InlineData("""[{"op":"line","from":[0,0]}]""", "op 1 (line): needs point 'to'")]
    [InlineData("""[{"op":"line","from":[0,0],"to":[1,1],"start":[0,0]}]""", "unknown key 'start'")]
    [InlineData("""[{"op":"line","from":[0,"a"],"to":[1,1]}]""", "'from' must be a point")]
    [InlineData("""[{"op":"circle","center":[0,0],"r":0}]""", "'r' must be > 0")]
    [InlineData("""[{"op":"hatch","pattern":"ANSI31"}]""", "needs 'ids' (closed boundaries) or 'pts'")]
    [InlineData("""[{"op":"line","from":[0,0],"to":[1,1],"color":"purple-ish"}]""", "Bad color")]
    [InlineData("""[]""", "ops is empty")]
    [InlineData("""not json""", "not valid JSON")]
    public void RejectsWithUsefulMessage(string json, string expected) =>
        Assert.Contains(expected, Assert.Throws<ToolError>(() => Ops.Parse(json)).Message);

    [Fact]
    public void LimitsBatchSize() =>
        Assert.Contains("max 2", Assert.Throws<ToolError>(() => Ops.Parse("""[{"op":"erase","ids":"1"},{"op":"erase","ids":"2"},{"op":"erase","ids":"3"}]""", 2)).Message);

    [Theory]
    [InlineData("ByLayer", "bylayer", 0)]
    [InlineData("7", "index", 7)]
    [InlineData("red", "index", 1)]
    [InlineData("255,128,0", "rgb", 0)]
    public void ParsesColors(string text, string kind, short index)
    {
        var c = ColorSpec.Parse(text);
        Assert.Equal(kind, c.Kind);
        Assert.Equal(index, c.Index);
    }
}

public class RouterTests
{
    sealed class Host : IAcadHost
    {
        public ToolOutput Status(Args a, McpConfig c) => new("ok");
        public ToolOutput Query(Args a, McpConfig c) => throw new ToolError("bad filter");
        public ToolOutput View(Args a, McpConfig c) => new("shot", [1, 2, 3]);
        public ToolOutput Export(Args a, McpConfig c) => throw new InvalidOperationException("boom");
        public ToolOutput Modify(Args a, McpConfig c) => new("modified");
        public ToolOutput RunScript(Args a, McpConfig c) => new(new string('x', 20000));
    }

    static CallReply Call(McpConfig cfg, string tool) =>
        new Router(new Host(), () => cfg).Handle(new CallRequest(tool, JsonDocument.Parse("{}").RootElement));

    [Fact]
    public void WritesAndScriptsAreOffByDefault()
    {
        var m = Call(new McpConfig(), "modify");
        Assert.False(m.Ok);
        Assert.Contains("allowWrite", m.Error);
        Assert.Contains("allowScripts", Call(new McpConfig(), "run_script").Error);
    }

    [Fact]
    public void WritesWorkWhenEnabled() => Assert.Equal("modified", Call(new McpConfig { AllowWrite = true }, "acad_modify").Text);

    [Fact]
    public void ErrorsBecomeShortText()
    {
        Assert.Equal("Error: bad filter", Call(new McpConfig(), "query").Error);
        Assert.Equal("Error: InvalidOperationException: boom", Call(new McpConfig(), "export").Error);
        Assert.Contains("Unknown tool", Call(new McpConfig(), "nope").Error);
    }

    [Fact]
    public void ImagesAreBase64() => Assert.Equal("AQID", Call(new McpConfig(), "view").Image);

    [Fact]
    public void OutputIsCapped()
    {
        var r = Call(new McpConfig { AllowScripts = true, MaxChars = 8000 }, "run_script");
        Assert.True(r.Text!.Length <= 8000);
        Assert.EndsWith("use a filter, a smaller limit, or export.", r.Text);
    }
}

public class ArgsTests
{
    [Fact]
    public void LenientTypes()
    {
        var a = Args.Parse("""{"Limit":"20","dry_run":"false","ids":["2A","2B"],"empty":"  "}""");
        Assert.Equal(20, a.Int("limit", 50, 1, 500));
        Assert.False(a.Bool("dry_run", true));
        Assert.Equal(["2A", "2B"], a.List("ids"));
        Assert.Null(a.Str("empty"));
        Assert.Equal(500, Args.Parse("""{"limit":9999}""").Int("limit", 50, 1, 500));
        Assert.Throws<ToolError>(() => Args.Parse("""{"limit":"lots"}""").Int("limit", 50, 1, 500));
    }
}

public class ConfigTests
{
    [Fact]
    public void BrokenConfigStaysReadOnly()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ allowWrite: true,,, ");
        Assert.False(McpConfig.Load(path).AllowWrite);
        File.WriteAllText(path, """{ "allowWrite": true, "allowScripts": false }""");
        Assert.True(McpConfig.Load(path).AllowWrite);
        Assert.False(McpConfig.Load(path + ".missing").AllowWrite);
    }

    [Fact]
    public void SessionDeleteOnlyRemovesOwnFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        new SessionInfo(1234, "t", 42, "0.1.0").Write(path);
        SessionInfo.Delete(7, path);
        Assert.Equal(1234, SessionInfo.Read(path)!.Port);
        SessionInfo.Delete(42, path);
        Assert.Null(SessionInfo.Read(path));
    }
}

public class LocalServerTests
{
    [Fact]
    public async Task RoundTripAndTokenCheck()
    {
        using var server = new LocalServer(req => Task.FromResult(new CallReply(true, $"got {req.Tool} {req.Args.GetProperty("x").GetInt32()}")));
        server.Start();
        using var http = new HttpClient();
        var body = new StringContent("""{"tool":"query","args":{"x":5}}""");

        var bad = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/call") { Content = body };
        bad.Headers.Add("X-Mcp-Token", "wrong");
        Assert.Equal(401, (int)(await http.SendAsync(bad)).StatusCode);

        var good = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/call") { Content = new StringContent("""{"tool":"query","args":{"x":5}}""") };
        good.Headers.Add("X-Mcp-Token", server.Token);
        var res = await http.SendAsync(good);
        var reply = JsonSerializer.Deserialize<CallReply>(await res.Content.ReadAsStringAsync(), Json.Options)!;
        Assert.True(reply.Ok);
        Assert.Equal("got query 5", reply.Text);
    }
}
