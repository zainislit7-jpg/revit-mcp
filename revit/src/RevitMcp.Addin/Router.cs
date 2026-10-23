using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Addin.Handlers;
using RevitMcp.Core;

namespace RevitMcp.Addin;

/// <summary>Maps tool names to handlers and applies the config gate on every call.</summary>
public sealed class Router(Dispatcher dispatcher)
{
    private static readonly TimeSpan Short = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    public static int Calls;
    public static DateTime? LastCall;
    public static string? LastTool;

    public async Task<CallResponse> Handle(CallRequest req)
    {
        Interlocked.Increment(ref Calls);
        LastCall = DateTime.Now;
        LastTool = req.Tool;
        // Re-read each call so flipping allowWrite takes effect without restarting Revit.
        var config = McpConfig.Load();
        var args = new Args(req.Args);
        try
        {
            switch (req.Tool)
            {
                case "status":
                    return await dispatcher.Run(app => StatusHandler.Run(app, config), Short);
                case "query":
                    return await dispatcher.Run(app => QueryHandler.Run(Doc(app), Ui(app), args, config), Short);
                case "view":
                    return await dispatcher.Run(app => ViewHandler.Run(Doc(app), Ui(app), args), Short);
                case "export":
                    return await dispatcher.Run(app => ExportHandler.Run(Doc(app), Ui(app), args, config), Long);
                case "modify":
                    var dryRun = args.Bool("dry_run", true);
                    if (!dryRun && !config.AllowWrite) return CallResponse.Fail(WriteOff);
                    var ops = ModifyHandler.Parse(args.Str("ops"));
                    return await dispatcher.Run(app => ModifyHandler.Run(Doc(app), ops, dryRun), Long);
                case "run_script":
                    if (!config.AllowScripts) return CallResponse.Fail(ScriptsOff);
                    var commit = args.Bool("commit", false);
                    if (commit && !config.AllowWrite) return CallResponse.Fail(WriteOff);
                    // Compile off the API thread; only execution needs Revit.
                    var script = ScriptHandler.Compile(args.Str("code"));
                    return await dispatcher.Run(app => ScriptHandler.Run(app, script, commit, config), Long);
                default:
                    return CallResponse.Fail($"Error: unknown tool '{req.Tool}'.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return CallResponse.Fail("Error: " + ex.Message);
        }
    }

    private const string WriteOff =
        "Error: writes are off. To allow them, set \"allowWrite\": true in %APPDATA%\\ClaudeMcp\\revit\\config.json.";

    private const string ScriptsOff =
        "Error: scripts are off. To allow them, set \"allowScripts\": true in %APPDATA%\\ClaudeMcp\\revit\\config.json.";

    private static UIDocument Ui(UIApplication app) =>
        app.ActiveUIDocument ?? throw new InvalidOperationException("No model is open in Revit.");

    private static Document Doc(UIApplication app) => Ui(app).Document;
}
