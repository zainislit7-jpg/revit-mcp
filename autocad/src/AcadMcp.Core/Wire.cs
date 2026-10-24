using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcadMcp.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Bridge -> add-in request body for POST /call.</summary>
public sealed record CallRequest(string Tool, JsonElement Args);

/// <summary>Add-in -> bridge reply. Image is a base64 PNG.</summary>
public sealed record CallReply(bool Ok, string? Text = null, string? Image = null, string? Error = null)
{
    public static CallReply Fail(string error) => new(false, Error: error);
}

/// <summary>Written by the add-in while it is listening; read by the bridge on every call.</summary>
public sealed record SessionInfo(int Port, string Token, int Pid, string Version, string App = Paths.App)
{
    public void Write(string? path = null)
    {
        path ??= Paths.SessionFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static SessionInfo? Read(string? path = null)
    {
        path ??= Paths.SessionFile;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionInfo>(File.ReadAllText(path), Json.Options) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Deletes the session file only if it still belongs to this process.</summary>
    public static void Delete(int pid, string? path = null)
    {
        path ??= Paths.SessionFile;
        if (Read(path)?.Pid == pid) File.Delete(path);
    }
}
