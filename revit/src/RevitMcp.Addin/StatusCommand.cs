using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin;

/// <summary>Ribbon button: connection state, call activity and the config gates, in one dialog.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var config = McpConfig.Load();
        var listening = App.Port is { } port ? $"Listening on 127.0.0.1:{port}" : "Not listening (the add-in failed to start)";
        var last = Router.LastCall is { } at ? $"{Router.Calls} so far, last: {Router.LastTool} at {at:HH:mm:ss}" : "none yet";

        var dialog = new TaskDialog("Claude MCP")
        {
            MainInstruction = App.Port.HasValue ? "Claude MCP is running" : "Claude MCP is not running",
            MainContent =
                $"{listening}\n" +
                $"Requests from Claude: {last}\n\n" +
                $"Edits (revit_modify): {(config.AllowWrite ? "allowed" : "off")}\n" +
                $"Scripts (revit_run_script): {(config.AllowScripts ? "allowed" : "off")}\n\n" +
                $"Settings: {McpPaths.ConfigFile}\n" +
                $"Add-in {App.Version}",
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
