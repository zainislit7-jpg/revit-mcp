using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RevitMcp.Bridge;
using RevitMcp.Core;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol, so all logging goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var config = McpConfig.Load();
var mcp = builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "revit", Version = typeof(ToolSet).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" })
    .WithStdioServerTransport()
    .WithTools((IEnumerable<Type>)ToolSet.For(config));

await builder.Build().RunAsync();

namespace RevitMcp.Bridge
{
    /// <summary>
    /// Only tools the config allows are listed, so a read-only setup never pays for (or tempts
    /// the model with) write schemas. The add-in checks the config again; it is the real gate.
    /// </summary>
    public static class ToolSet
    {
        public static Type[] For(McpConfig config)
        {
            var types = new List<Type> { typeof(ReadTools) };
            if (config.AllowWrite) types.Add(typeof(WriteTools));
            if (config.AllowScripts) types.Add(typeof(ScriptTools));
            return types.ToArray();
        }
    }
}
