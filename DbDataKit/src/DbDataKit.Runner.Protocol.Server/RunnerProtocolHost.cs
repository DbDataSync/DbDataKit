using DbDataKit.Runner.Kit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>Where the host listens and how long a token lives.</summary>
public sealed class RunnerProtocolHostOptions
{
    /// <summary>
    /// Linux: the directory of the socket. It is created with mode 0700 (an existing one must already allow no one but its owner) and the socket in it gets 0600. Null: a new directory under the temp folder, removed when the
    /// host is disposed. Windows: unused (a named pipe has no directory).
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>The file name of the socket in <see cref="Directory"/>. Default <c>runner.sock</c>.</summary>
    public string SocketName { get; init; } = "runner.sock";

    /// <summary>Windows: the pipe's name. Null: a name with a random part.</summary>
    public string? PipeName { get; init; }

    /// <summary>How long a token works when its grant gives no lifetime and the run does not end first. Default 24 hours.</summary>
    public TimeSpan DefaultTokenLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The clock token expiry reads. Default: the system clock.</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>
/// The runner's one listener for task processes (plan 28a DR3): a gRPC server on a Unix domain socket in a 0700 directory (socket mode 0600) on Linux, or a named pipe on Windows, and never a TCP port. A task
/// authenticates with the per-run token <see cref="Register"/> returns (the runner puts it, and <see cref="Endpoint"/>, in the task's environment), and that token reaches only that run. The same host serves every task.
/// </summary>
public sealed class RunnerProtocolHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RunSessions _sessions;
    private readonly string? _ownedDirectory;

    private RunnerProtocolHost(WebApplication app, RunSessions sessions, RunnerEndpoint endpoint, string? ownedDirectory)
    {
        _app = app;
        _sessions = sessions;
        Endpoint = endpoint;
        _ownedDirectory = ownedDirectory;
    }

    /// <summary>Where tasks connect.</summary>
    public RunnerEndpoint Endpoint { get; }

    /// <summary>The addresses the server listens on, for a check that none is a TCP address: <c>http://unix:/path</c> or <c>http://pipe:/name</c>.</summary>
    public IReadOnlyList<string> ListeningOn => [.. _app.Urls];

    /// <summary>Starts listening. Throws when the socket's directory is broader than its owner, with the path and the mode.</summary>
    public static async Task<RunnerProtocolHost> StartAsync(RunnerProtocolHostOptions? options = null, CancellationToken ct = default)
    {
        options ??= new RunnerProtocolHostOptions();
        var time = options.Time ?? TimeProvider.System;
        var sessions = new RunSessions(time, options.DefaultTokenLifetime);
        string? owned = null;
        RunnerEndpoint endpoint;
        string? socketPath = null;
        if (OperatingSystem.IsWindows())
        {
            endpoint = RunnerEndpoint.Pipe(options.PipeName ?? "dbdatakit-runner-" + Guid.NewGuid().ToString("N"));
        }
        else
        {
            var directory = PrepareDirectory(options.Directory, out var created);
            owned = created ? directory : null;
            socketPath = Path.Combine(directory, options.SocketName);
            if (socketPath.Length > 100) throw new ArgumentException($"The socket path '{socketPath}' is {socketPath.Length} characters; a Unix socket path is limited to about 108. Use a shorter directory.", nameof(options));
            if (File.Exists(socketPath)) File.Delete(socketPath);
            endpoint = RunnerEndpoint.Unix(socketPath);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            if (socketPath is not null) kestrel.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
            else kestrel.ListenNamedPipe(endpoint.Address, listen => listen.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddSingleton(sessions);
        builder.Services.AddGrpc(grpc =>
        {
            grpc.Interceptors.Add<RunTokenInterceptor>();
            grpc.MaxReceiveMessageSize = 64 * 1024 * 1024;
            grpc.EnableDetailedErrors = false;
        });
        var app = builder.Build();
        app.MapGrpcService<RunnerProtocolService>();
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
            if (socketPath is not null && !OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            if (owned is not null) TryDelete(owned);
            throw;
        }

        return new RunnerProtocolHost(app, sessions, endpoint, owned);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string PrepareDirectory(string? requested, out bool created)
    {
        created = false;
        if (requested is null)
        {
            created = true;
            var temp = System.IO.Directory.CreateTempSubdirectory("dbdatakit-runner-");
            File.SetUnixFileMode(temp.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return temp.FullName;
        }

        var full = Path.GetFullPath(requested);
        if (System.IO.Directory.Exists(full))
        {
            var mode = File.GetUnixFileMode(full);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException($"The socket directory '{full}' has mode {Convert.ToString((int)mode, 8)}; a runner's socket directory allows its owner only (0700). Change it with chmod 700, or choose another directory.");
        }
        else
        {
            created = true;
            System.IO.Directory.CreateDirectory(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return full;
    }

    /// <summary>
    /// Issues the token for one run. The task is given the token and <see cref="Endpoint"/> in its environment; the token reaches that run's progress, steps, units, payloads and log and nothing else, and stops
    /// working when the run ends, when <see cref="Unregister"/> is called, or when its lifetime is over.
    /// </summary>
    public string Register(RunGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var problems = grant.Start.Problems().ToList();
        if (grant.Start.ProtocolVersion != RunnerProtocol.V1) problems.Add($"The start message names the protocol '{grant.Start.ProtocolVersion}'; this host serves '{RunnerProtocol.V1}'.");
        if (string.IsNullOrWhiteSpace(grant.Chain)) problems.Add("The attempt chain is empty.");
        if (grant.Environment is null) problems.Add("The run has no environment (the state database it works in).");
        else if (grant.Environment.Package != grant.Start.Task.Package || grant.Environment.Channel != grant.Start.Task.Channel)
            problems.Add($"The run is for {grant.Start.Task} and its environment keeps progress under {grant.Environment.Package}/{grant.Environment.Channel}.");
        if (problems.Count > 0) throw new ArgumentException("The run cannot be granted: " + string.Join(" ", problems), nameof(grant));
        return _sessions.Add(grant).Token;
    }

    /// <summary>
    /// The runner is done with a token (the process is gone, or the runner gave up on it): a live token stops working and an ended one is forgotten, with what it reported. Calls with it are refused from now on. False
    /// when the token was neither.
    /// </summary>
    public bool Unregister(string token)
    {
        // a process that died holding claims on its owner's work: they go back to the queue now, so the owner's next run finds them without waiting out the lease (plan 28c E11)
        var session = _sessions.FindEvenIfEnded(token);
        if (session is { WorkUsed: true })
        {
            try
            {
                session.Work.ReleaseAll();
            }
            catch (Exception)
            {
                // best effort: the lease runs out and the next claim releases what is left
            }
        }

        return _sessions.Forget(token, "The run token was withdrawn by the runner.");
    }

    /// <summary>Asks the run to stop: pushed to a task that is watching, and answered to its next heartbeat. The task has the grant's cancellation grace to finish. False when the token is not live.</summary>
    public bool Cancel(string token, string? reason = null) => _sessions.Find(token)?.RequestCancel(reason ?? "The runner asked this task to stop.") ?? false;

    /// <summary>What the run reported one way, and whether it started, ended or was told to stop; available after the run ended until <see cref="Unregister"/>. Null for a token that was never issued or is forgotten.</summary>
    public RunTelemetry? TelemetryOf(string token) => _sessions.FindEvenIfEnded(token)?.Telemetry();

    /// <summary>
    /// Completes when the run behind the token is over for the protocol: the task sent its end (see <see cref="RunTelemetry.End"/>), the token was withdrawn or it expired. Completes at once for a token that is already
    /// ended, and for one this host never issued or has forgotten.
    /// </summary>
    public Task WaitForEndAsync(string token) => _sessions.FindEvenIfEnded(token)?.EndSignal.Task ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        if (_ownedDirectory is not null) TryDelete(_ownedDirectory);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a socket directory left behind holds nothing but a dead socket
        }
    }
}
