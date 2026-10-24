using AcadMcp.Core;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(AcadMcp.Addin.Plugin))]
[assembly: CommandClass(typeof(AcadMcp.Addin.Commands))]

namespace AcadMcp.Addin;

/// <summary>Starts the localhost listener when AutoCAD loads the plugin and advertises it in session.json.</summary>
public sealed class Plugin : IExtensionApplication
{
    public static string Version => typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    static LocalServer? _server;
    static MainThread? _ui;

    public void Initialize()
    {
        try
        {
            Start();
        }
        catch (System.Exception e)
        {
            Say($"\nAcadMcp failed to start: {e.Message}");
        }
    }

    public void Terminate() => Stop();

    internal static void Start()
    {
        Stop();
        _ui ??= new MainThread(); // created on AutoCAD's UI thread during Initialize
        var router = new Router(new AcadHost(), () => McpConfig.Load());
        _server = new LocalServer(req => _ui.Run(() => router.Handle(req), TimeSpan.FromMinutes(9)));
        _server.Start();
        new SessionInfo(_server.Port, _server.Token, Environment.ProcessId, Version).Write();
        Say($"\nAcadMcp {Version} ready for Claude on 127.0.0.1:{_server.Port} ({McpConfig.Load().ModeLine}).");
    }

    internal static void Stop()
    {
        _server?.Dispose();
        _server = null;
        try { SessionInfo.Delete(Environment.ProcessId); } catch (System.Exception) { /* best effort */ }
    }

    internal static string Describe() =>
        _server is null
            ? "AcadMcp is stopped. Run CLAUDEMCPRESTART."
            : $"AcadMcp {Version} on 127.0.0.1:{_server.Port} · {McpConfig.Load().ModeLine} · config {Paths.ConfigFile}";

    internal static void Say(string msg) => AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(msg);
}

public sealed class Commands
{
    [CommandMethod("CLAUDEMCP")]
    public void Status() => Plugin.Say("\n" + Plugin.Describe());

    [CommandMethod("CLAUDEMCPRESTART")]
    public void Restart() => Plugin.Start();
}
