using System.Net;
using System.Net.Sockets;

namespace DbDataKit.Tools.XmlaForwarder;

/// <summary>
/// A TCP pass-through: it accepts connections on <see cref="Port"/>, opens one to the target for each, copies the bytes both ways without changing them and
/// hands every chunk to a <see cref="SessionRecorder"/>. It does not read the protocol, so whatever the client and the server negotiate (login, compression,
/// signing) works as it does without the forwarder; a client that checks the server's name or address sees the forwarder's.
/// </summary>
public sealed class TcpForwarder : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly SessionRecorder _recorder;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _accepting;
    private int _disposed;

    private TcpForwarder(TcpListener listener, string targetHost, int targetPort, SessionRecorder recorder)
    {
        (_listener, _targetHost, _targetPort, _recorder) = (listener, targetHost, targetPort, recorder);
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>The port clients connect to (the chosen one when 0 was asked for).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Starts listening on <paramref name="bind"/>:<paramref name="port"/> (0 for any free port) and forwarding to the target.</summary>
    public static TcpForwarder Start(IPAddress bind, int port, string targetHost, int targetPort, SessionRecorder recorder)
    {
        var listener = new TcpListener(bind, port);
        listener.Start();
        return new TcpForwarder(listener, targetHost, targetPort, recorder);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            lock (_connections) _connections.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        var id = _recorder.Open(client.Client.RemoteEndPoint?.ToString() ?? "?", $"{_targetHost}:{_targetPort}");
        string? error = null;
        try
        {
            using var server = new TcpClient();
            await server.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            var toServer = Pump(client, server, id, Direction.Client);
            var toClient = Pump(server, client, id, Direction.Server);
            await Task.WhenAll(toServer, toClient);
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            error = e.Message;
        }
        finally { _recorder.Close(id, error); }
    }

    /// <summary>Copies from one side to the other until the source ends, then ends the other side's sending (a half close), so the peer sees the end as it would have.</summary>
    private async Task Pump(TcpClient from, TcpClient to, int id, Direction direction)
    {
        var buffer = new byte[64 * 1024];
        var source = from.GetStream();
        var target = to.GetStream();
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, _stop.Token)) > 0)
            {
                _recorder.Chunk(id, direction, buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
            to.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // a reset on one side ends both: close the other so its pump stops
            to.Close();
            from.Close();
        }
    }

    /// <summary>Stops accepting, ends the open connections and waits for their files to be written.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
        Task[] open;
        lock (_connections) open = [.. _connections];
        try { await Task.WhenAll(open); } catch (OperationCanceledException) { /* the stop */ }
        _stop.Dispose();
    }
}
