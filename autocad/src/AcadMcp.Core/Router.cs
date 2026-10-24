using System.Text.Json;

namespace AcadMcp.Core;

/// <summary>The app-side implementation of the six tools.</summary>
public interface IAcadHost
{
    ToolOutput Status(Args a, McpConfig c);
    ToolOutput Query(Args a, McpConfig c);
    ToolOutput View(Args a, McpConfig c);
    ToolOutput Export(Args a, McpConfig c);
    ToolOutput Modify(Args a, McpConfig c);
    ToolOutput RunScript(Args a, McpConfig c);
}

/// <summary>Enforces config gates and output caps, and turns exceptions into short error text.</summary>
public sealed class Router(IAcadHost host, Func<McpConfig> config)
{
    public CallReply Handle(CallRequest req)
    {
        var cfg = config();
        var tool = req.Tool.StartsWith(Paths.App + "_", StringComparison.Ordinal) ? req.Tool[(Paths.App.Length + 1)..] : req.Tool;
        var args = req.Args.ValueKind == JsonValueKind.Object ? new Args(req.Args) : Args.Empty;
        try
        {
            var output = tool switch
            {
                "status" => host.Status(args, cfg),
                "query" => host.Query(args, cfg),
                "view" => host.View(args, cfg),
                "export" => host.Export(args, cfg),
                "modify" when !cfg.AllowWrite => throw new ToolError(
                    $"Writes are off. Set \"allowWrite\": true in {Paths.ConfigFile} to enable modify."),
                "modify" => host.Modify(args, cfg),
                "run_script" when !cfg.AllowScripts => throw new ToolError(
                    $"Scripts are off. Set \"allowScripts\": true in {Paths.ConfigFile} to enable run_script."),
                "run_script" => host.RunScript(args, cfg),
                _ => throw new ToolError($"Unknown tool '{req.Tool}'."),
            };
            return new CallReply(true, Cap(output.Text, cfg.MaxChars), output.Png is null ? null : Convert.ToBase64String(output.Png));
        }
        catch (ToolError e)
        {
            return CallReply.Fail("Error: " + e.Message);
        }
        catch (Exception e)
        {
            return CallReply.Fail($"Error: {e.GetType().Name}: {e.Message}");
        }
    }

    public static string Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var cut = text.LastIndexOf('\n', maxChars - 60);
        if (cut < maxChars / 2) cut = maxChars - 60;
        return text[..cut] + "\n… truncated, use a filter, a smaller limit, or export.";
    }
}
