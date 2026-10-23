using System.Text.Json;

namespace AcadMcp.Core;

/// <summary>User settings in config.json. Missing file = read-only defaults.</summary>
public sealed record McpConfig
{
    public bool AllowWrite { get; init; }
    public bool AllowScripts { get; init; }
    public string? ExportDir { get; init; }
    public int MaxChars { get; init; } = 8000;

    public string ExportFolder => string.IsNullOrWhiteSpace(ExportDir) ? Paths.DefaultExportDir : ExportDir!;

    public string ModeLine =>
        $"mode: {(AllowWrite ? "read-write" : "read-only")}, scripts {(AllowScripts ? "on" : "off")}";

    public static McpConfig Load(string? path = null)
    {
        path ??= Paths.ConfigFile;
        try
        {
            if (!File.Exists(path)) return new McpConfig();
            return JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(path), Json.Options) ?? new McpConfig();
        }
        catch (Exception)
        {
            // A broken config must never unlock writes.
            return new McpConfig();
        }
    }
}
