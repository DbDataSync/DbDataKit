using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Apache.Arrow.Flight.Client;
using DbDataKit.Hosting.Children;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Worker;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Data;

/// <summary>
/// Owns the 32-bit worker process: starts it on first use, keeps it for later queries, restarts it if it dies and
/// stops it with the service. The worker listens on a loopback port and only answers calls carrying a per-launch secret.
/// The process is launched through <see cref="ChildProcessHost"/>: it lives in a Windows job object (it dies with the
/// service even if the service is killed), its output is drained into the log, and disposing the host kills its tree.
/// The worker keeps its own Flight server (hence no handshake transport); the wire protocol is unchanged.
/// </summary>
public sealed class WorkerProcessHost(IOptionsMonitor<ProxyOptions> options, ILogger<WorkerProcessHost> log)
    : IWorkerLink, IWorkerExecutorFactory, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChildProcess? _child;
    private GrpcChannel? _channel;
    private FlightClient? _client;
    private string _secret = "";

    /// <summary>Process id of the running worker, or null when none is running.</summary>
    public int? WorkerProcessId => _child is { Exited.IsCompleted: false } c ? c.ProcessId : null;

    public IQueryExecutor Create(string connectionString) => new WorkerQueryExecutor(this, connectionString);

    public async ValueTask<(FlightClient Client, string Secret)> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_child is null || _child.Exited.IsCompleted || _client is null)
                await StartAsync(cancellationToken).ConfigureAwait(false);
            return (_client!, _secret);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string DefaultWorkerPath => Path.Combine(AppContext.BaseDirectory, "worker", "DbDataProxy.Worker.exe");

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        await StopProcessAsync().ConfigureAwait(false);

        var settings = options.CurrentValue.Worker;
        string path = settings.Path ?? DefaultWorkerPath;
        if (!File.Exists(path))
            throw new RpcException(new Status(StatusCode.Unavailable,
                "The 32-bit worker is not installed. Expected " + path + " (see Proxy:Worker:Path)."));

        int port = FreeLoopbackPort();
        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        ChildProcess child;
        try
        {
            child = ChildProcessHost.Launch(new ChildProcessSpec
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                Arguments = ["--port", port.ToString(), "--parent", Environment.ProcessId.ToString()],
                Environment = new Dictionary<string, string> { ["DBPROXY_WORKER_SECRET"] = _secret },
                Transport = ChildEndpointKind.None,
                OnOutputLine = line => log.LogDebug("worker: {Line}", line),
                OnErrorLine = line => log.LogWarning("worker: {Line}", line),
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Could not start the 32-bit worker: " + ex.Message));
        }

        try
        {
            await WaitUntilListeningAsync(child, port, TimeSpan.FromSeconds(settings.StartupTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await child.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _child = child;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new FlightClient(_channel);
        log.LogInformation("Started 32-bit worker (pid {Pid}) on port {Port}", child.ProcessId, port);
    }

    private static async Task WaitUntilListeningAsync(ChildProcess process, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            if (process.Exited.IsCompleted)
                throw new RpcException(new Status(StatusCode.Unavailable, $"The 32-bit worker exited during startup (code {process.Exited.Result.ExitCode})."));
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "Timed out waiting for the 32-bit worker to start."));
            }
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private async ValueTask StopProcessAsync()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
        if (_child is not null)
        {
            var child = _child;
            _child = null;
            await child.DisposeAsync().ConfigureAwait(false); // kills the worker and anything it started
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopProcessAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
