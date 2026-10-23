using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RevitMcp.Core;

/// <summary>Bridge -> add-in request: POST /call.</summary>
public sealed record CallRequest(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("args")] JsonObject? Args);

/// <summary>Add-in -> bridge reply.</summary>
public sealed record CallResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("image")] string? Image = null,
    [property: JsonPropertyName("error")] string? Error = null)
{
    public static CallResponse Success(string text, string? imageBase64 = null) => new(true, text, imageBase64);
    public static CallResponse Fail(string error) => new(false, Error: error);
}

/// <summary>Written by the add-in so the bridge can find it.</summary>
public sealed record SessionInfo(
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("version")] string Version);

/// <summary>User settings, %APPDATA%\ClaudeMcp\revit\config.json.</summary>
public sealed class McpConfig
{
    [JsonPropertyName("allowWrite")] public bool AllowWrite { get; set; }
    [JsonPropertyName("allowScripts")] public bool AllowScripts { get; set; }
    [JsonPropertyName("maxChars")] public int MaxChars { get; set; } = Limits.MaxChars;
    [JsonPropertyName("exportDir")] public string? ExportDir { get; set; }

    public static McpConfig Load(string? path = null)
    {
        path ??= McpPaths.ConfigFile;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(path), Json.Options) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}

public static class Limits
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    public const int MaxChars = 8000;
    public const int MaxImagePx = 1600;
    public const int DefaultImagePx = 768;
}

public static class McpPaths
{
    public const string App = "revit";

    /// <summary>CLAUDEMCP_HOME overrides both base folders (tests, portable installs).</summary>
    private static string Base(Environment.SpecialFolder folder) =>
        Environment.GetEnvironmentVariable("CLAUDEMCP_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(folder), "ClaudeMcp");

    public static string SessionFile => Path.Combine(Base(Environment.SpecialFolder.LocalApplicationData), App, "session.json");

    public static string ConfigFile => Path.Combine(Base(Environment.SpecialFolder.ApplicationData), App, "config.json");

    public static string DefaultExportDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ClaudeMcp", "exports");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
