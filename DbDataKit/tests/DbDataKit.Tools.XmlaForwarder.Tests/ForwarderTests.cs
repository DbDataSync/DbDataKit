using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DbDataKit.Tools.XmlaForwarder;

namespace DbDataKit.Tools.XmlaForwarder.Tests;

public class ForwarderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("xmla-forwarder-").FullName;

    public void Dispose() { try { Directory.Delete(_folder, recursive: true); } catch (IOException) { } }

    /// <summary>A server that answers each message with "reply to " plus the message, as UTF-16 like the XML of an XMLA message, until the client ends the connection.</summary>
    private static (TcpListener Listener, Task Serving) Server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); } catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    var stream = c.GetStream();
                    var buffer = new byte[4096];
                    int read;
                    while ((read = await stream.ReadAsync(buffer)) > 0)
                    {
                        var request = Encoding.Unicode.GetString(buffer, 0, read);
                        await stream.WriteAsync(Encoding.Unicode.GetBytes("reply to " + request));
                    }
                });
            }
        });
        return (listener, serving);
    }

    private static async Task<string> Ask(int port, string message)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Unicode.GetBytes(message));
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer);
        client.Client.Shutdown(SocketShutdown.Send);
        // the server ends its side after the client does: the forwarder must pass the end on
        Assert.Equal(0, await stream.ReadAsync(buffer));
        return Encoding.Unicode.GetString(buffer, 0, read);
    }

    [Fact]
    public async Task TheBytesArriveUnchangedAndAreRecordedPerConnectionAndDirection()
    {
        var (server, serving) = Server();
        using var recorder = new SessionRecorder(_folder);
        await using (var forwarder = TcpForwarder.Start(IPAddress.Loopback, 0, "127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, recorder))
        {
            Assert.Equal("reply to <Discover/>", await Ask(forwarder.Port, "<Discover/>"));
            Assert.Equal("reply to <Execute/>", await Ask(forwarder.Port, "<Execute/>"));
        }
        server.Stop();
        await serving;

        Assert.Equal(2, recorder.Connections);
        Assert.Equal(Encoding.Unicode.GetBytes("<Discover/>"), await File.ReadAllBytesAsync(Path.Combine(_folder, SessionRecorder.StreamFile(1, Direction.Client))));
        Assert.Equal(Encoding.Unicode.GetBytes("reply to <Discover/>"), await File.ReadAllBytesAsync(Path.Combine(_folder, SessionRecorder.StreamFile(1, Direction.Server))));
        Assert.Equal(Encoding.Unicode.GetBytes("<Execute/>"), await File.ReadAllBytesAsync(Path.Combine(_folder, SessionRecorder.StreamFile(2, Direction.Client))));

        // the text view lists the XML found in the UTF-16 stream
        Assert.Contains("utf16 reply to <Execute/>", await File.ReadAllTextAsync(Path.Combine(_folder, "conn-0002.server.txt")));
    }

    [Fact]
    public async Task TheIndexHasOneLinePerEventInOrder()
    {
        var (server, serving) = Server();
        using (var recorder = new SessionRecorder(_folder))
        {
            await using var forwarder = TcpForwarder.Start(IPAddress.Loopback, 0, "127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port, recorder);
            await Ask(forwarder.Port, "<a/>");
        }
        server.Stop();
        await serving;

        var lines = File.ReadAllLines(Path.Combine(_folder, "session.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal("open", lines[0].GetProperty("event").GetString());
        Assert.Equal("close", lines[^1].GetProperty("event").GetString());
        var data = lines.Where(l => l.GetProperty("event").GetString() == "data").ToList();
        // the request goes before the reply, each with the offset and length it has in its stream file
        Assert.Equal("client", data[0].GetProperty("dir").GetString());
        Assert.Equal(0, data[0].GetProperty("offset").GetInt64());
        Assert.Equal(Encoding.Unicode.GetByteCount("<a/>"), data[0].GetProperty("length").GetInt32());
        Assert.Equal("server", data[1].GetProperty("dir").GetString());
        Assert.All(lines, l => Assert.True(DateTime.TryParse(l.GetProperty("t").GetString(), out _)));
    }

    [Fact]
    public async Task ATargetThatIsNotThereClosesTheConnectionAndRecordsWhy()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();   // nothing listens there now
        using (var recorder = new SessionRecorder(_folder))
        {
            await using var forwarder = TcpForwarder.Start(IPAddress.Loopback, 0, "127.0.0.1", port, recorder);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, forwarder.Port);
            var stream = client.GetStream();
            // the forwarder cannot reach the target, so it closes the client's connection
            var read = await Task.Run(() => { try { return stream.Read(new byte[1]); } catch (IOException) { return 0; } });
            Assert.Equal(0, read);
        }
        var close = File.ReadAllLines(Path.Combine(_folder, "session.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).Single(l => l.GetProperty("event").GetString() == "close");
        Assert.NotEqual(JsonValueKind.Null, close.GetProperty("error").ValueKind);
    }

    [Theory]
    [InlineData("localhost:2383", "localhost", 2383)]
    [InlineData("[::1]:2383", "::1", 2383)]
    [InlineData("srv.example.com:80", "srv.example.com", 80)]
    public void TargetsAreHostAndPort(string text, string host, int port)
    {
        Assert.True(TargetParser.TryParse(text, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData(":2383")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("localhost:abc")]
    public void ATargetWithoutAValidPortIsRefused(string text) => Assert.False(TargetParser.TryParse(text, out _, out _));

    [Fact]
    public void ReadableTextIsFoundAsUtf8AndUtf16AndShortRunsAreLeftOut()
    {
        var bytes = new List<byte> { 0x01, 0x02 };
        bytes.AddRange(Encoding.UTF8.GetBytes("<Envelope>utf8</Envelope>"));
        bytes.AddRange([0xFF, 0x00, 0xFE]);
        bytes.Add(0x00);   // so the UTF-16 run below starts at an odd offset
        bytes.AddRange(Encoding.Unicode.GetBytes("<Discover>utf16</Discover>"));
        bytes.AddRange(Encoding.UTF8.GetBytes("\u0001short\u0001"));
        var runs = StreamText.Runs(bytes.ToArray());
        Assert.Contains("<Envelope>utf8</Envelope>", runs);
        Assert.Contains("<Discover>utf16</Discover>", runs);
        Assert.DoesNotContain("short", runs);
        Assert.Contains("# none", StreamText.Describe([1, 2, 3, 4]));
    }
}
