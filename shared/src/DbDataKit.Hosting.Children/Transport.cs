using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Hosting.Children;

public enum ChildEndpointKind
{
    NamedPipe,
    UnixSocket,
    LoopbackTcp,
}

/// <summary>Where the child connects. Serialised as <c>pipe:NAME</c>, <c>unix:/path</c> or <c>tcp:127.0.0.1:PORT</c>.</summary>
public sealed record ChildEndpoint(ChildEndpointKind Kind, string Address)
{
    public override string ToString() => Kind switch
    {
        ChildEndpointKind.NamedPipe => $"pipe:{Address}",
        ChildEndpointKind.UnixSocket => $"unix:{Address}",
        _ => $"tcp:{Address}",
    };

    public static ChildEndpoint Parse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0) throw new FormatException($"Invalid endpoint '{text}'.");
        var rest = text[(colon + 1)..];
        return text[..colon] switch
        {
            "pipe" => new(ChildEndpointKind.NamedPipe, rest),
            "unix" => new(ChildEndpointKind.UnixSocket, rest),
            "tcp" => new(ChildEndpointKind.LoopbackTcp, rest),
            _ => throw new FormatException($"Unknown endpoint kind in '{text}'."),
        };
    }
}

/// <summary>Host side. The endpoint exists (is bound) before the child is launched, so there is no allocate-then-release port race.</summary>
public interface IChildTransportListener : IAsyncDisposable
{
    ChildEndpoint Endpoint { get; }

    /// <summary>Accept one connection that proves knowledge of <paramref name="token"/>; others are dropped.</summary>
    Task<Stream> AcceptAsync(string token, CancellationToken cancellationToken);
}

public static class ChildTransports
{
    /// <summary>Named pipe on Windows, Unix-domain socket elsewhere; loopback TCP only when asked for.</summary>
    public static IChildTransportListener CreateListener(ChildEndpointKind? preferred = null)
    {
        var kind = preferred ?? (OperatingSystem.IsWindows() ? ChildEndpointKind.NamedPipe : ChildEndpointKind.UnixSocket);
        return kind switch
        {
            ChildEndpointKind.NamedPipe => new NamedPipeListener(),
            ChildEndpointKind.UnixSocket => new UnixSocketListener(),
            _ => new LoopbackTcpListener(),
        };
    }

    /// <summary>Child side: connect and authenticate with the per-launch token.</summary>
    public static async Task<Stream> ConnectAsync(ChildLaunchInfo info, CancellationToken cancellationToken = default) =>
        await ConnectAsync(info.Endpoint, info.Token, cancellationToken).ConfigureAwait(false);

    public static async Task<Stream> ConnectAsync(ChildEndpoint endpoint, string token, CancellationToken cancellationToken = default)
    {
        Stream stream;
        switch (endpoint.Kind)
        {
            case ChildEndpointKind.NamedPipe:
                var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);
                stream = pipe;
                break;
            case ChildEndpointKind.UnixSocket:
                var u = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await u.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken).ConfigureAwait(false);
                stream = new NetworkStream(u, ownsSocket: true);
                break;
            default:
                var t = new TcpClient();
                var hostPort = endpoint.Address.Split(':');
                await t.ConnectAsync(IPAddress.Parse(hostPort[0]), int.Parse(hostPort[1], System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                stream = t.GetStream();
                break;
        }

        try
        {
            await Handshake.ClientAsync(stream, token, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// Token handshake used on every transport (it is what guards the loopback-TCP fallback, where any local process
/// could otherwise connect): client sends [u16 length][token], server answers one byte, 1 = accepted.
/// </summary>
internal static class Handshake
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task ClientAsync(Stream s, string token, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var frame = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)bytes.Length);
        bytes.CopyTo(frame, 2);
        await s.WriteAsync(frame, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
        var reply = new byte[1];
        if (await s.ReadAsync(reply, ct).ConfigureAwait(false) != 1 || reply[0] != 1)
        {
            throw new UnauthorizedAccessException("The host rejected the child token.");
        }
    }

    public static async Task<bool> ServerAsync(Stream s, string token, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            var header = new byte[2];
            await s.ReadExactlyAsync(header, cts.Token).ConfigureAwait(false);
            var len = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (len is 0 or > 512) return false;
            var got = new byte[len];
            await s.ReadExactlyAsync(got, cts.Token).ConfigureAwait(false);
            var expected = Encoding.UTF8.GetBytes(token);
            var ok = got.Length == expected.Length && CryptographicOperations.FixedTimeEquals(got, expected);
            await s.WriteAsync(new byte[] { ok ? (byte)1 : (byte)0 }, cts.Token).ConfigureAwait(false);
            await s.FlushAsync(cts.Token).ConfigureAwait(false);
            return ok;
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }
}

internal sealed class NamedPipeListener : IChildTransportListener
{
    private readonly string _name = "dbdata-" + Guid.NewGuid().ToString("N");
    private NamedPipeServerStream _pending;

    public NamedPipeListener()
    {
        _pending = Create(); // exists before the child is launched
        Endpoint = new ChildEndpoint(ChildEndpointKind.NamedPipe, _name);
    }

    public ChildEndpoint Endpoint { get; }

    private NamedPipeServerStream Create() =>
        new(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    public async Task<Stream> AcceptAsync(string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = _pending;
            _pending = Create();
            await current.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (await Handshake.ServerAsync(current, token, cancellationToken).ConfigureAwait(false))
            {
                return current;
            }

            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _pending.DisposeAsync();
}

internal sealed class UnixSocketListener : IChildTransportListener
{
    private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly string _directory;

    public UnixSocketListener()
    {
        // CreateTempSubdirectory gives a private (0700) directory on Unix; the socket inside is also 0600.
        _directory = Directory.CreateTempSubdirectory("dbdata-").FullName;
        var path = Path.Combine(_directory, "c.sock");
        _socket.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _socket.Listen(4);
        Endpoint = new ChildEndpoint(ChildEndpointKind.UnixSocket, path);
    }

    public ChildEndpoint Endpoint { get; }

    public async Task<Stream> AcceptAsync(string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await _socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(client, ownsSocket: true);
            if (await Handshake.ServerAsync(stream, token, cancellationToken).ConfigureAwait(false))
            {
                return stream;
            }

            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}

internal sealed class LoopbackTcpListener : IChildTransportListener
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public LoopbackTcpListener()
    {
        // Bound and held for the life of the launch: nobody else can take the port between choosing and use.
        _listener.Start(4);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new ChildEndpoint(ChildEndpointKind.LoopbackTcp, $"127.0.0.1:{port}");
    }

    public ChildEndpoint Endpoint { get; }

    public async Task<Stream> AcceptAsync(string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var stream = client.GetStream();
            if (await Handshake.ServerAsync(stream, token, cancellationToken).ConfigureAwait(false))
            {
                return stream;
            }

            client.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
