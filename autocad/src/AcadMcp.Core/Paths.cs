namespace AcadMcp.Core;

/// <summary>Where the add-in and bridge find each other. See shared/mcp-conventions.md.</summary>
public static class Paths
{
    public const string App = "acad";

    static string? Home => Environment.GetEnvironmentVariable("CLAUDEMCP_HOME") is { Length: > 0 } h ? h : null;

    static string Folder(Environment.SpecialFolder f) =>
        Home is { } h ? Path.Combine(h, App) : Path.Combine(Environment.GetFolderPath(f), "ClaudeMcp", App);

    public static string SessionFile => Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "session.json");
    public static string ConfigFile => Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "config.json");

    public static string DefaultExportDir
    {
        get
        {
            if (Home is { } h) return Path.Combine(h, "exports");
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(docs, "ClaudeMcp", "exports");
        }
    }
}
