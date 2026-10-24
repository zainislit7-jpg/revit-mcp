using System.Net.Http.Json;
using System.Text.Json;
using AcadMcp.Core;
using ModelContextProtocol.Protocol;

namespace AcadMcp.Bridge;

/// <summary>Forwards a tool call to the AutoCAD add-in found via session.json.</summary>
public sealed class AddinClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static string NotConnected =>
        "AutoCAD is not connected. Open AutoCAD 2027 with the AcadMcp plugin loaded " +
        "(installed bundle, or NETLOAD AcadMcp.Addin.dll), then retry. " +
        $"Looked for {Paths.SessionFile}.";

    public async Task<CallToolResult> Call(string tool, Dictionary<string, object?> args, CancellationToken ct)
    {
        var session = SessionInfo.Read();
        if (session is null) return Text(NotConnected, error: tool != "status");

        var payload = new CallRequest(tool, JsonSerializer.SerializeToElement(
            args.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value), Json.Options));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{session.Port}/call")
        {
            // Explicit bytes so Content-Length is set; the add-in's tiny HTTP reader does not do chunked bodies.
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options))
            {
                Headers = { ContentType = new("application/json") },
            },
        };
        req.Headers.Add("X-Mcp-Token", session.Token);

        CallReply? reply;
        try
        {
            using var res = await Http.SendAsync(req, ct);
            reply = await res.Content.ReadFromJsonAsync<CallReply>(Json.Options, ct);
        }
        catch (HttpRequestException)
        {
            // Stale session.json from a crashed AutoCAD, or the plugin was unloaded.
            return Text(NotConnected, error: tool != "status");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Text("Error: AutoCAD did not answer in time. It may be busy in a command or a dialog; press Esc in AutoCAD and retry.", true);
        }

        if (reply is null) return Text("Error: empty reply from AutoCAD.", true);
        if (!reply.Ok) return Text(reply.Error ?? "Error: unknown failure.", true);

        var result = Text(reply.Text ?? "", false);
        if (reply.Image is { } b64) result.Content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(b64), "image/png"));
        return result;
    }

    static CallToolResult Text(string text, bool error) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = error ? true : null };
}
