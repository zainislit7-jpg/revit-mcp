using System.Text.Json;
using AcadMcp.Core;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace AcadMcp.Tests;

/// <summary>In-memory stand-in for AutoCAD so the real bridge process can be driven end to end.</summary>
sealed class FakeDrawing : IAcadHost
{
    static readonly string[] Fields = ["Handle", "Type", "Layer", "Length", "Space"];

    readonly List<IRow> _rows = Enumerable.Range(1, 1200).Select(i => (IRow)new DictRow(new Dictionary<string, string?>
    {
        ["Handle"] = (0x2A00 + i).ToString("X"),
        ["Type"] = i % 4 == 0 ? "INSERT" : "LINE",
        ["Layer"] = $"A-WALL-{i % 7}",
        ["Length"] = Fmt.N(i * 12.5),
        ["Space"] = "Model",
    })).ToList();

    public ToolOutput Status(Args a, McpConfig c) => new($"AutoCAD fake · {c.ModeLine}\nUnits: mm");

    public ToolOutput Query(Args a, McpConfig c)
    {
        var f = Filter.Parse(a.Str("filter"), Fields);
        return new(a.Str("mode", "summary") == "summary"
            ? Core.Query.Summary("Entities", _rows, f, Core.Query.Fields(a.Str("group"), ["Type"], Fields)[0], a.Int("limit", 30, 1, 500), "units mm")
            : Core.Query.List("Entities", _rows, f, Core.Query.Fields(a.Str("fields"), ["Handle", "Type", "Layer"], Fields),
                a.Int("limit", 50, 1, 500), Core.Query.Offset(a.Str("cursor")), c.MaxChars, "units mm"));
    }

    public ToolOutput View(Args a, McpConfig c) => new("Screenshot 768x480", Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="));

    public ToolOutput Export(Args a, McpConfig c) => new("Wrote 1200 rows to C:\\exports\\a.csv");
    public ToolOutput Modify(Args a, McpConfig c) => new($"Dry run OK: {Ops.Parse(a.Str("ops")).Count} ops valid");
    public ToolOutput RunScript(Args a, McpConfig c) => new("42");
}

public sealed class BridgeTests(ITestOutputHelper log) : IAsyncLifetime
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "claudemcp-test-" + Guid.NewGuid().ToString("N"));
    LocalServer? _server;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_home, Paths.App));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _server?.Dispose();
        Directory.Delete(_home, true);
        return Task.CompletedTask;
    }

    void StartFakeAutoCad(McpConfig? cfg = null)
    {
        var router = new Router(new FakeDrawing(), () => cfg ?? new McpConfig());
        _server = new LocalServer(req => Task.FromResult(router.Handle(req)));
        _server.Start();
        new SessionInfo(_server.Port, _server.Token, Environment.ProcessId, "test").Write(Path.Combine(_home, Paths.App, "session.json"));
    }

    void WriteConfig(object cfg) => File.WriteAllText(Path.Combine(_home, Paths.App, "config.json"), JsonSerializer.Serialize(cfg));

    async Task<McpClient> Connect()
    {
        var bridge = Path.Combine(AppContext.BaseDirectory, "AcadMcp.Bridge.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "autocad",
            Command = "dotnet",
            Arguments = [bridge],
            EnvironmentVariables = new Dictionary<string, string?> { ["CLAUDEMCP_HOME"] = _home },
        });
        return await McpClient.CreateAsync(transport);
    }

    static string TextOf(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task ReadOnlyByDefaultListsFourTools()
    {
        await using var client = await Connect();
        var tools = await client.ListToolsAsync();
        Assert.Equal(["acad_export", "acad_query", "acad_status", "acad_view"], tools.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task ConfigAddsWriteAndScriptTools()
    {
        WriteConfig(new { allowWrite = true, allowScripts = true });
        await using var client = await Connect();
        var tools = await client.ListToolsAsync();
        Assert.Contains("acad_modify", tools.Select(t => t.Name));
        Assert.Contains("acad_run_script", tools.Select(t => t.Name));
    }

    [Fact]
    public async Task NotConnectedIsExplained()
    {
        await using var client = await Connect();
        var status = await client.CallToolAsync("acad_status");
        Assert.NotEqual(true, status.IsError);
        Assert.Contains("AutoCAD is not connected", TextOf(status));
        var query = await client.CallToolAsync("acad_query");
        Assert.True(query.IsError);
    }

    [Fact]
    public async Task StaleSessionFileIsTreatedAsNotConnected()
    {
        new SessionInfo(1, "x", 999999, "old").Write(Path.Combine(_home, Paths.App, "session.json"));
        await using var client = await Connect();
        Assert.Contains("not connected", TextOf(await client.CallToolAsync("acad_status")));
    }

    [Fact]
    public async Task QueryFlowsThroughAddin()
    {
        StartFakeAutoCad();
        await using var client = await Connect();

        var summary = TextOf(await client.CallToolAsync("acad_query", new Dictionary<string, object?> { ["group"] = "Layer", ["limit"] = 3 }));
        log.WriteLine(summary);
        Assert.StartsWith("Entities: 1,200 in 7 Layer groups · units mm\nLayer|Count\n", summary);

        var page = TextOf(await client.CallToolAsync("acad_query", new Dictionary<string, object?>
        {
            ["mode"] = "list", ["filter"] = "Type=INSERT; Layer=A-WALL-1", ["fields"] = "Handle,Length", ["limit"] = 5,
        }));
        log.WriteLine(page);
        Assert.StartsWith("Entities: 43 (showing 1-5) next:5 · units mm\nHandle|Length\n", page);

        var bad = await client.CallToolAsync("acad_query", new Dictionary<string, object?> { ["mode"] = "list", ["fields"] = "Colour" });
        Assert.True(bad.IsError);
        Assert.StartsWith("Error: Unknown field 'Colour'", TextOf(bad));
    }

    [Fact]
    public async Task ScreenshotComesBackAsImage()
    {
        StartFakeAutoCad();
        await using var client = await Connect();
        var r = await client.CallToolAsync("acad_view", new Dictionary<string, object?> { ["action"] = "screenshot" });
        var img = Assert.Single(r.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", img.MimeType);
    }

    [Fact]
    public async Task AddinRefusesWritesEvenIfBridgeAdvertisesThem()
    {
        // Bridge config allows writes, but the add-in's own config does not: the add-in wins.
        WriteConfig(new { allowWrite = true });
        StartFakeAutoCad(new McpConfig());
        await using var client = await Connect();
        var r = await client.CallToolAsync("acad_modify", new Dictionary<string, object?> { ["ops"] = """[{"op":"erase","ids":"2A01"}]""" });
        Assert.True(r.IsError);
        Assert.Contains("Writes are off", TextOf(r));
    }

    [Fact]
    public async Task ToolListStaysUnderTokenBudget()
    {
        WriteConfig(new { allowWrite = true, allowScripts = true });
        await using var client = await Connect();
        var tools = await client.ListToolsAsync();
        var json = JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool), Json.Options);
        // ~4 chars per token is a fair estimate for English + JSON schema.
        var tokens = json.Length / 4;
        foreach (var t in tools)
            log.WriteLine($"{t.Name}: ~{JsonSerializer.Serialize(t.ProtocolTool, Json.Options).Length / 4} tokens");
        log.WriteLine($"total ~{tokens} tokens ({json.Length} chars)");
        Assert.True(tokens < 3000, $"Tool list is ~{tokens} tokens; budget is 3000.");
    }
}
