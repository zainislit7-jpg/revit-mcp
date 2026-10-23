using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMcp.Core;

/// <summary>Bridge side: finds the running add-in through session.json and forwards calls.</summary>
public sealed class AddinClient(string? sessionFile = null, TimeSpan? timeout = null)
{
    private readonly string _sessionFile = sessionFile ?? McpPaths.SessionFile;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = timeout ?? TimeSpan.FromMinutes(5),
    };

    public const string NotRunning =
        "Error: Revit is not running, or the Claude MCP add-in is not loaded. Open Revit 2027 and a model, then retry.";

    public async Task<CallResponse> CallAsync(string tool, JsonObject? args, CancellationToken ct = default)
    {
        // Re-read every call so a Revit restart (new port and token) needs no bridge restart.
        SessionInfo? session;
        try
        {
            session = File.Exists(_sessionFile)
                ? JsonSerializer.Deserialize<SessionInfo>(await File.ReadAllTextAsync(_sessionFile, ct), Json.Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            session = null;
        }
        if (session is null) return CallResponse.Fail(NotRunning);

        using var msg = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{session.Port}/call")
        {
            // Buffered (not streamed) so Content-Length is sent; the add-in's server does not do chunked bodies.
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new CallRequest(tool, args), Json.Options))
            {
                Headers = { ContentType = new("application/json") },
            },
        };
        msg.Headers.Add("X-Mcp-Token", session.Token);
        try
        {
            using var resp = await _http.SendAsync(msg, ct);
            return await resp.Content.ReadFromJsonAsync<CallResponse>(Json.Options, ct)
                   ?? CallResponse.Fail($"Error: empty reply from add-in (HTTP {(int)resp.StatusCode}).");
        }
        catch (HttpRequestException)
        {
            return CallResponse.Fail(NotRunning);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return CallResponse.Fail("Error: Revit did not answer in time. It may be busy or showing a dialog.");
        }
    }
}
