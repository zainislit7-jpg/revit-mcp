using Autodesk.Revit.UI;
using RevitMcp.Core;

namespace RevitMcp.Addin;

/// <summary>
/// Entry point. Starts a localhost listener and publishes session.json; every call is
/// marshalled onto Revit's API thread through one ExternalEvent.
/// </summary>
public sealed class App : IExternalApplication
{
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    private LocalServer? _server;

    /// <summary>Port the listener is on, for the ribbon's status dialog.</summary>
    public static int? Port { get; private set; }

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            var dispatcher = new Dispatcher();
            var router = new Router(dispatcher);
            _server = new LocalServer(router.Handle);
            _server.WriteSessionFile(Version);
            Port = _server.Port;
            AddRibbon(application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("Claude MCP", "The Claude MCP add-in could not start:\n" + ex.Message);
            return Result.Failed;
        }
    }

    /// <summary>A visible "Claude MCP" panel on the Add-ins tab, so people can see the add-in is loaded.</summary>
    private static void AddRibbon(UIControlledApplication application)
    {
        var panel = application.CreateRibbonPanel("Claude MCP");
        var button = new PushButtonData("ClaudeMcpStatus", "Claude\nStatus", typeof(App).Assembly.Location, typeof(StatusCommand).FullName)
        {
            ToolTip = "Shows whether Claude can reach Revit, and whether edits and scripts are allowed.",
        };
        panel.AddItem(button);
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _server?.Dispose();
        LocalServer.DeleteSessionFile();
        return Result.Succeeded;
    }
}
