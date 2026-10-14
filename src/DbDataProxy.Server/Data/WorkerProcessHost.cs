using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Apache.Arrow.Flight.Client;
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
/// </summary>
public sealed class WorkerProcessHost(IOptionsMonitor<ProxyOptions> options, ILogger<WorkerProcessHost> log)
    : IWorkerLink, IWorkerExecutorFactory, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private GrpcChannel? _channel;
    private FlightClient? _client;
    private string _secret = "";

    public IQueryExecutor Create(string connectionString) => new WorkerQueryExecutor(this, connectionString);

    public async ValueTask<(FlightClient Client, string Secret)> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited || _client is null)
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
        StopProcess();

        var settings = options.CurrentValue.Worker;
        string path = settings.Path ?? DefaultWorkerPath;
        if (!File.Exists(path))
            throw new RpcException(new Status(StatusCode.Unavailable,
                "The 32-bit worker is not installed. Expected " + path + " (see Proxy:Worker:Path)."));

        int port = FreeLoopbackPort();
        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.Environment["DBPROXY_WORKER_SECRET"] = _secret;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the 32-bit worker.");
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.LogDebug("worker: {Line}", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) log.LogWarning("worker: {Line}", e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await WaitUntilListeningAsync(process, port, TimeSpan.FromSeconds(settings.StartupTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        _process = process;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions { MaxReceiveMessageSize = 64 * 1024 * 1024 });
        _client = new FlightClient(_channel);
        log.LogInformation("Started 32-bit worker (pid {Pid}) on port {Port}", process.Id, port);
    }

    private static async Task WaitUntilListeningAsync(Process process, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        while (true)
        {
            if (process.HasExited)
                throw new RpcException(new Status(StatusCode.Unavailable, $"The 32-bit worker exited during startup (code {process.ExitCode})."));
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

    private void StopProcess()
    {
        _channel?.Dispose();
        _channel = null;
        _client = null;
        if (_process is not null)
        {
            TryKill(_process);
            _process.Dispose();
            _process = null;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public ValueTask DisposeAsync()
    {
        StopProcess();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
