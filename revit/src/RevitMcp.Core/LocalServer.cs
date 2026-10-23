using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RevitMcp.Core;

/// <summary>
/// Minimal HTTP/1.1 server on 127.0.0.1 for POST /call. Built on TcpListener instead of
/// HttpListener so it needs no URL ACL or admin rights inside Revit.
/// </summary>
public sealed class LocalServer : IDisposable
{
    private const int MaxBody = 4 * 1024 * 1024;

    private readonly Func<CallRequest, Task<CallResponse>> _handler;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Token { get; }

    public LocalServer(Func<CallRequest, Task<CallResponse>> handler, int port = 0, string? token = null)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _ = AcceptLoop();
    }

    /// <summary>Writes session.json so the bridge can find this server.</summary>
    public void WriteSessionFile(string version, string? path = null)
    {
        path ??= McpPaths.SessionFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new SessionInfo(Port, Token, Environment.ProcessId, version)));
    }

    public static void DeleteSessionFile(string? path = null)
    {
        try { File.Delete(path ?? McpPaths.SessionFile); } catch (IOException) { }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        int status;
        CallResponse body;
        try
        {
            (status, body) = await Handle(stream);
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException)
        {
            (status, body) = (400, CallResponse.Fail("Bad request: " + ex.Message));
        }
        catch (Exception ex)
        {
            (status, body) = (500, CallResponse.Fail(ex.GetType().Name + ": " + ex.Message));
        }

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(body, Json.Options);
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {json.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(json);
        }
        catch (IOException) { }
    }

    private async Task<(int, CallResponse)> Handle(NetworkStream stream)
    {
        var (requestLine, headers, leftover) = await ReadHead(stream);
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || parts[0] != "POST" || parts[1] != "/call")
            return (404, CallResponse.Fail("Only POST /call is served."));

        // Browsers send Origin; the bridge never does. Refusing it blocks drive-by requests from web pages.
        if (headers.ContainsKey("origin"))
            return (403, CallResponse.Fail("Cross-origin requests are refused."));

        var token = headers.GetValueOrDefault("x-mcp-token") ?? "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
            return (401, CallResponse.Fail("Bad token."));

        if (!int.TryParse(headers.GetValueOrDefault("content-length"), out var length) || length < 0 || length > MaxBody)
            return (411, CallResponse.Fail("Content-Length required (max 4 MB)."));

        var buf = new byte[length];
        var have = Math.Min(leftover.Length, length);
        leftover.AsSpan(0, have).CopyTo(buf);
        await stream.ReadExactlyAsync(buf.AsMemory(have, length - have));

        var req = JsonSerializer.Deserialize<CallRequest>(buf, Json.Options)
                  ?? throw new FormatException("Empty body.");
        return (200, await _handler(req));
    }

    private static async Task<(string, Dictionary<string, string>, byte[])> ReadHead(NetworkStream stream)
    {
        var buf = new byte[16 * 1024];
        var len = 0;
        while (true)
        {
            if (len == buf.Length) throw new FormatException("Headers too large.");
            var n = await stream.ReadAsync(buf.AsMemory(len));
            if (n == 0) throw new IOException("Connection closed before headers ended.");
            len += n;
            var end = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;

            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            return (lines[0], headers, buf[(end + 4)..len]);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
