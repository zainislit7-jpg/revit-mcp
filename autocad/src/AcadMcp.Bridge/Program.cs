using AcadMcp.Bridge;
using AcadMcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// stdio MCP server. All logging goes to stderr so stdout stays pure JSON-RPC.
var config = McpConfig.Load();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton<AddinClient>();
var mcp = builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "autocad", Version = typeof(AddinClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" };
        o.ServerInstructions =
            "AutoCAD 2027. Call acad_status first. Query in steps: summary, then list with a filter, then get for a few handles. " +
            "Send large results to acad_export. Coordinates and lengths are drawing units (see status).";
    })
    .WithStdioServerTransport()
    .WithTools<ReadTools>();

// Only advertise what the config allows; the add-in checks again on every call.
if (config.AllowWrite) mcp.WithTools<WriteTools>();
if (config.AllowScripts) mcp.WithTools<ScriptTools>();

await builder.Build().RunAsync();
