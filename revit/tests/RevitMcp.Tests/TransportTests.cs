using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RevitMcp.Core;

namespace RevitMcp.Tests;

public sealed class TransportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("revitmcp").FullName;
    private string SessionFile => Path.Combine(_dir, "session.json");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task RoundTripsThroughSessionFile()
    {
        CallRequest? seen = null;
        using var server = new LocalServer(req => { seen = req; return Task.FromResult(CallResponse.Success("hello", "AAAA")); });
        server.WriteSessionFile("test", SessionFile);

        var r = await new AddinClient(SessionFile).CallAsync("query", new JsonObject { ["mode"] = "summary" });

        Assert.True(r.Ok, r.Error);
        Assert.Equal("hello", r.Text);
        Assert.Equal("AAAA", r.Image);
        Assert.Equal("query", seen!.Tool);
        Assert.Equal("summary", seen.Args!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task NoSessionFileMeansNotRunning()
    {
        var r = await new AddinClient(SessionFile).CallAsync("status", null);
        Assert.False(r.Ok);
        Assert.Equal(AddinClient.NotRunning, r.Error);
    }

    [Fact]
    public async Task StaleSessionFileMeansNotRunning()
    {
        using (var server = new LocalServer(_ => Task.FromResult(CallResponse.Success("x"))))
        {
            server.WriteSessionFile("test", SessionFile);
        }
        var r = await new AddinClient(SessionFile).CallAsync("status", null);
        Assert.False(r.Ok);
        Assert.Equal(AddinClient.NotRunning, r.Error);
    }

    [Fact]
    public async Task WrongTokenIsRejected()
    {
        var called = false;
        using var server = new LocalServer(_ => { called = true; return Task.FromResult(CallResponse.Success("x")); });
        File.WriteAllText(SessionFile, $$"""{"port":{{server.Port}},"token":"wrong","pid":1,"version":"t"}""");

        var r = await new AddinClient(SessionFile).CallAsync("status", null);
        Assert.False(r.Ok);
        Assert.Contains("token", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(called);
    }

    [Fact]
    public async Task BrowserOriginIsRejected()
    {
        var called = false;
        using var server = new LocalServer(_ => { called = true; return Task.FromResult(CallResponse.Success("x")); });
        var raw = await Raw(server.Port,
            $"POST /call HTTP/1.1\r\nHost: x\r\nOrigin: https://evil.example\r\nX-Mcp-Token: {server.Token}\r\nContent-Length: 15\r\n\r\n{{\"tool\":\"status\"}}"[..^2]);
        Assert.StartsWith("HTTP/1.1 403", raw);
        Assert.False(called);
    }

    [Fact]
    public async Task OnlyPostCallIsServed()
    {
        using var server = new LocalServer(_ => Task.FromResult(CallResponse.Success("x")));
        var raw = await Raw(server.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", raw);
    }

    [Fact]
    public void ListensOnLoopbackOnly()
    {
        using var server = new LocalServer(_ => Task.FromResult(CallResponse.Success("x")));
        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var mine = listeners.Where(l => l.Port == server.Port).ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, l => Assert.True(IPAddress.IsLoopback(l.Address)));
    }

    private static async Task<string> Raw(int port, string request)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(s);
        return await reader.ReadToEndAsync();
    }
}
