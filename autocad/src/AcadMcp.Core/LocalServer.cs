using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AcadMcp.Core;

/// <summary>
/// Minimal HTTP/1.1 endpoint bound to 127.0.0.1 only. One request per connection:
/// <c>POST /call</c> with header <c>X-Mcp-Token</c> and a <see cref="CallRequest"/> body.
/// Plain TcpListener (not HttpListener) so no URL ACL or admin rights are needed on Windows.
/// </summary>
public sealed class LocalServer : IDisposable
{
    const int MaxBody = 4 * 1024 * 1024;

    readonly Func<CallRequest, Task<CallReply>> _handler;
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public int Port { get; private set; }

    public LocalServer(Func<CallRequest, Task<CallReply>> handler) => _handler = handler;

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            var (method, path, headers, body) = await ReadRequest(stream);
            if (method != "POST" || path != "/call")
            {
                await Respond(stream, 404, new CallReply(false, Error: "Not found"));
                return;
            }
            if (!headers.TryGetValue("x-mcp-token", out var token) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token)))
            {
                await Respond(stream, 401, new CallReply(false, Error: "Bad token"));
                return;
            }
            var req = JsonSerializer.Deserialize<CallRequest>(body, Json.Options) ?? throw new InvalidDataException("empty body");
            await Respond(stream, 200, await _handler(req));
        }
        catch (Exception e)
        {
            try { await Respond(client.GetStream(), 400, CallReply.Fail("Error: " + e.Message)); } catch { /* client gone */ }
        }
    }

    static async Task<(string, string, Dictionary<string, string>, byte[])> ReadRequest(NetworkStream stream)
    {
        var buf = new List<byte>(4096);
        var one = new byte[1];
        // Read headers byte-wise up to the blank line; they are tiny.
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) throw new IOException("connection closed");
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^1] == '\n' && buf[^2] == '\r' && buf[^3] == '\n' && buf[^4] == '\r') break;
            if (buf.Count > 16 * 1024) throw new InvalidDataException("headers too large");
        }
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var start = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var i = l.IndexOf(':');
            if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
        }
        if (headers.ContainsKey("transfer-encoding")) throw new InvalidDataException("chunked bodies are not supported; send Content-Length");
        var len = headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out var n) ? n : 0;
        if (len > MaxBody) throw new InvalidDataException("body too large");
        var body = new byte[len];
        await stream.ReadExactlyAsync(body);
        return (start[0], start.Length > 1 ? start[1] : "/", headers, body);
    }

    static async Task Respond(NetworkStream stream, int status, CallReply reply)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(reply, Json.Options);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
