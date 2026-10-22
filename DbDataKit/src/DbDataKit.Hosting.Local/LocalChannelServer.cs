using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DbDataKit.Hosting.Local;

/// <summary>Answers one request per message on the local channel. The operating system decides who may connect: file modes on Unix, an access list on the pipe on Windows.</summary>
public sealed class LocalChannelServer : IAsyncDisposable
{
    private readonly LocalChannelOptions _options;
    private readonly Func<byte[], CancellationToken, Task<byte[]>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private Socket? _listener;
    private Task? _loop;

    public LocalChannelServer(LocalChannelOptions options, Func<byte[], CancellationToken, Task<byte[]>> handler)
    {
        options.Validate();
        _options = options;
        _handler = handler;
    }

    /// <summary>The pipe name or socket path clients connect to.</summary>
    public string Endpoint => _options.Endpoint;

    public void Start()
    {
        if (_loop is not null) throw new InvalidOperationException("The channel is already started.");
        _loop = OperatingSystem.IsWindows() ? AcceptPipesAsync() : AcceptSocketsAsync();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private async Task AcceptSocketsAsync()
    {
        var dir = _options.SocketDirectory;
        var dirMode = _options.GroupAccess ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                          : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, dirMode);
        File.SetUnixFileMode(dir, dirMode);
        var path = Path.Combine(dir, "channel.sock");
        if (File.Exists(path)) File.Delete(path);

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, _options.GroupAccess ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                                                        : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        _listener = listener;
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { break; }
            Track(ServeAsync(new NetworkStream(client, ownsSocket: true)));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async Task AcceptPipesAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = CreatePipe();
            try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception) when (_stop.IsCancellationRequested) { await pipe.DisposeAsync().ConfigureAwait(false); break; }
            Track(ServeAsync(pipe));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        foreach (var identity in _options.WindowsIdentities)
            security.AddAccessRule(new PipeAccessRule(Resolve(identity), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // The creator is added too, so the server can open further instances of the pipe.
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(_options.Endpoint, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static SecurityIdentifier Resolve(string identity) => identity.ToUpperInvariant() switch
    {
        "SYSTEM" => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        "ADMINISTRATORS" => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        _ => (SecurityIdentifier)new NTAccount(identity).Translate(typeof(SecurityIdentifier)),
    };

    private void Track(Task task)
    {
        lock (_connections)
        {
            _connections.RemoveAll(t => t.IsCompleted);
            _connections.Add(task);
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                while (await LocalChannelFraming.ReadAsync(stream, _options.MaxMessageBytes, _stop.Token).ConfigureAwait(false) is { } request)
                {
                    var response = await _handler(request, _stop.Token).ConfigureAwait(false);
                    await LocalChannelFraming.WriteAsync(stream, response, _options.MaxMessageBytes, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // A client that goes away or misbehaves loses its own connection, nothing more.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener?.Dispose();
        if (_loop is not null) try { await _loop.ConfigureAwait(false); } catch (Exception) { }
        Task[] open;
        lock (_connections) open = [.. _connections];
        try { await Task.WhenAll(open).ConfigureAwait(false); } catch (Exception) { }
        if (!OperatingSystem.IsWindows() && _listener is not null) try { File.Delete(_options.Endpoint); } catch (IOException) { }
        _stop.Dispose();
    }
}
