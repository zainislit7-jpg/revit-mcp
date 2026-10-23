using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RevitMcp.Core;

namespace RevitMcp.Tests;

/// <summary>Starts the real bridge over stdio against a fake add-in.</summary>
public sealed class BridgeTests : IAsyncLifetime
{
    private readonly string _home = Directory.CreateTempSubdirectory("revitmcp-home").FullName;
    private readonly List<CallRequest> _calls = [];
    private LocalServer _addin = null!;

    public Task InitializeAsync()
    {
        _addin = new LocalServer(req =>
        {
            lock (_calls) _calls.Add(req);
            return Task.FromResult(req.Tool switch
            {
                "view" => CallResponse.Success("Level 1 [FloorPlan]", Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47])),
                "modify" => CallResponse.Fail("Error: writes are off."),
                _ => CallResponse.Success($"{req.Tool} ok"),
            });
        });
        _addin.WriteSessionFile("test", Path.Combine(_home, "revit", "session.json"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _addin.Dispose();
        Directory.Delete(_home, true);
        return Task.CompletedTask;
    }

    private async Task<McpClient> Start(bool write = false, bool scripts = false)
    {
        Directory.CreateDirectory(Path.Combine(_home, "revit"));
        File.WriteAllText(Path.Combine(_home, "revit", "config.json"),
            JsonSerializer.Serialize(new McpConfig { AllowWrite = write, AllowScripts = scripts }));
        var bridge = Path.Combine(AppContext.BaseDirectory, "RevitMcp.Bridge.dll");
        return await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "revit",
            Command = "dotnet",
            Arguments = [bridge],
            EnvironmentVariables = new Dictionary<string, string?> { ["CLAUDEMCP_HOME"] = _home },
        }));
    }

    [Fact]
    public async Task ReadOnlyConfigListsOnlyReadTools()
    {
        await using var client = await Start();
        var names = (await client.ListToolsAsync()).Select(t => t.Name).Order().ToList();
        Assert.Equal(["revit_export", "revit_query", "revit_status", "revit_view"], names);
    }

    [Fact]
    public async Task FullConfigListsSixTools()
    {
        await using var client = await Start(write: true, scripts: true);
        Assert.Equal(6, (await client.ListToolsAsync()).Count);
    }

    [Fact]
    public async Task ForwardsOnlyGivenArguments()
    {
        await using var client = await Start();
        var r = await client.CallToolAsync("revit_query", new Dictionary<string, object?> { ["mode"] = "list", ["category"] = "Walls", ["limit"] = 20 });
        Assert.Equal("query ok", Assert.IsType<TextContentBlock>(Assert.Single(r.Content)).Text);
        var call = _calls.Single(c => c.Tool == "query");
        Assert.Equal(["category", "limit", "mode"], call.Args!.Select(p => p.Key).Order().ToList());
        Assert.Equal(20, call.Args!["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task ScreenshotComesBackAsImage()
    {
        await using var client = await Start();
        var r = await client.CallToolAsync("revit_view", new Dictionary<string, object?> { ["action"] = "screenshot" });
        var img = Assert.IsType<ImageContentBlock>(r.Content[0]);
        Assert.Equal("image/png", img.MimeType);
        Assert.Contains(r.Content, c => c is TextContentBlock t && t.Text.Contains("Level 1"));
    }

    [Fact]
    public async Task AddinErrorsBecomeToolErrors()
    {
        await using var client = await Start(write: true);
        var r = await client.CallToolAsync("revit_modify", new Dictionary<string, object?> { ["ops"] = "[]", ["dry_run"] = false });
        Assert.True(r.IsError);
        Assert.Equal(false, _calls.Single(c => c.Tool == "modify").Args!["dry_run"]!.GetValue<bool>());
    }

    /// <summary>
    /// Tool schemas ride along with every request. This pins them: an accidental description
    /// change fails here. Run with UPDATE_SNAPSHOTS=1 to accept a deliberate change.
    /// </summary>
    [Fact]
    public async Task ToolSchemasMatchSnapshotAndBudget()
    {
        await using var client = await Start(write: true, scripts: true);
        var tools = (await client.ListToolsAsync()).Select(t => t.ProtocolTool).OrderBy(t => t.Name).ToList();
        var json = JsonSerializer.Serialize(tools, new JsonSerializerOptions(McpJson.Options) { WriteIndented = true });

        // ~4 chars per token; the design budget is under 3k tokens for the whole server.
        var compact = JsonSerializer.Serialize(tools, McpJson.Options);
        Assert.True(compact.Length / 4 < 3000, $"Tool list is ~{compact.Length / 4} tokens, over the 3k budget.");

        var path = Path.Combine(SnapshotDir(), "tools.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1" || !File.Exists(path))
        {
            File.WriteAllText(path, json);
            return;
        }
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings(), json.ReplaceLineEndings());
    }

    private static string SnapshotDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.Combine(Path.GetDirectoryName(file)!, "Snapshots");
}

internal static class McpJson
{
    public static readonly JsonSerializerOptions Options = ModelContextProtocol.McpJsonUtilities.DefaultOptions;
}
