using System.IO.Pipes;
using System.Net.Sockets;

namespace DbDataKit.Hosting.Local;

/// <summary>The command-line side of the local channel.</summary>
public sealed class LocalChannelClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _max;

    private LocalChannelClient(Stream stream, int max) { _stream = stream; _max = max; }

    /// <summary>Connects to the named server. Throws <see cref="UnauthorizedAccessException"/> when the operating system refuses this user, and <see cref="IOException"/> or <see cref="TimeoutException"/> when nothing is listening.</summary>
    public static async Task<LocalChannelClient> ConnectAsync(LocalChannelOptions options, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        options.Validate();
        timeout ??= TimeSpan.FromSeconds(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout.Value);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var pipe = new NamedPipeClientStream(".", options.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(cts.Token).ConfigureAwait(false); }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
                return new LocalChannelClient(pipe, options.MaxMessageBytes);
            }
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.Endpoint), cts.Token).ConfigureAwait(false); }
            catch { socket.Dispose(); throw; }
            return new LocalChannelClient(new NetworkStream(socket, ownsSocket: true), options.MaxMessageBytes);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Nothing answered on {options.Endpoint} within {timeout.Value.TotalSeconds:0.#}s.");
        }
    }

    public async Task<byte[]> SendAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        await LocalChannelFraming.WriteAsync(_stream, request, _max, ct).ConfigureAwait(false);
        return await LocalChannelFraming.ReadAsync(_stream, _max, ct).ConfigureAwait(false)
               ?? throw new EndOfStreamException("The server closed the channel without answering.");
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
