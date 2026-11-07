using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataRunner.Server;

/// <summary>
/// The runner inside the server process (plan 28a R10), a hosted service. At start it finds the task host, listens on the socket tasks talk to (in <c>run/</c> in the server's directory, mode 0700), registers this instance in the
/// <c>runner</c> module with what the machine can enforce (<see cref="LimitCapabilities"/>), marks the runs a dead process left as interrupted, and starts the scheduler loop on the materialization cache under <c>cache/</c>. It
/// renews the registration every <see cref="RunnerSettings.InstanceHeartbeat"/> (and registers again if the group marked it gone). At stop it ends the loop with its grace period (running tasks are cancelled, their items go back to
/// the queue), marks the instance gone and closes the socket, so a stop leaves no instance live and no run running.
/// </summary>
public sealed class RunnerEngine(RunnerSettings settings, StateDatabase state, ILogger? logger = null) : IHostedService, IAsyncDisposable
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private RunnerProtocolHost? _host;
    private Task? _loop;
    private Task? _beat;
    private Guid? _instance;
    private RunnerControl? _control;
    private IReadOnlyDictionary<string, string> _capabilities = new Dictionary<string, string>();

    /// <summary>The control over the tasks; null until the engine has started.</summary>
    public RunnerControl? Control => _control;

    /// <summary>This process's row in the group's instances; null until the engine has started and after it has stopped.</summary>
    public Guid? InstanceId => _instance;

    /// <summary>What this machine reports about itself (memory and CPU limits, the operating system, the network).</summary>
    public IReadOnlyDictionary<string, string> Capabilities => _capabilities;

    public string TaskHostPath { get; private set; } = "";

    /// <summary>The channels this runner serves.</summary>
    public IReadOnlyList<string> Channels => settings.Channels;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // a program that cannot start a task is not started: the missing file is named now, not at the first run
        TaskHostPath = RunnerTaskHost.Require(settings.TaskHostPath);
        Directory.CreateDirectory(settings.CacheDirectory);
        Directory.CreateDirectory(settings.ScratchDirectory);

        _host = await RunnerProtocolHost.StartAsync(new RunnerProtocolHostOptions { Directory = settings.SocketDirectory }, cancellationToken).ConfigureAwait(false);
        var executor = new TaskExecutor(state, _host, new TaskExecutorOptions
        {
            DataDirectory = settings.Directory,
            TaskHostPath = TaskHostPath,
            Service = RunnerState.EngineService,
            Redactor = new Redactor(),
            Logger = _log,
        });

        var instances = new RunnerInstances(state);
        // the runs a process that died left behind: those of a SQLite state are this runner's alone; a group's are only marked when no instance of it is live (another one may be running them)
        if (!RunnerState.IsGroup(state) || instances.ListLive(settings.InstanceLease).Count == 0)
        {
            var interrupted = executor.InterruptStaleRuns(state.Time.GetUtcNow());
            if (interrupted > 0) _log.LogWarning("{Count} run(s) of an earlier process were still marked running and are now marked interrupted.", interrupted);
        }

        _capabilities = LimitCapabilities.Detect();
        Register(instances);

        var payloads = new PayloadStore(state);
        var scheduler = new RunnerScheduler(state, executor,
            new ArtifactCache(settings.CacheDirectory, new PayloadArtifactSource(payloads)),
            new DefinitionVersionSource(new DefinitionStore(state), new DefinitionPublisher(state, RunnerPackages.Kinds(), payloads, "runner")),
            new RunnerSchedulerOptions
            {
                Channels = settings.Channels,
                Service = RunnerState.EngineService,
                Tick = settings.Tick,
                CancellationGrace = settings.CancellationGrace,
                Logger = _log,
            });
        _control = new RunnerControl(state, scheduler);
        _loop = Task.Run(() => scheduler.RunAsync(_stopping.Token));
        _beat = Task.Run(() => HeartbeatAsync(instances, _stopping.Token));
        _log.LogInformation("Runner {Instance} serving the channels {Channels} (task host {TaskHost}).", _instance, string.Join(", ", settings.Channels), TaskHostPath);
    }

    private void Register(RunnerInstances instances)
    {
        var id = instances.Register(RunnerState.ServiceId, Environment.MachineName, typeof(RunnerEngine).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        instances.ReplaceCapabilities(id, _capabilities);
        lock (_gate) _instance = id;
    }

    private async Task HeartbeatAsync(RunnerInstances instances, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(settings.InstanceHeartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (_instance is { } id && !instances.Heartbeat(id))
                    {
                        _log.LogWarning("The instance {Instance} was marked gone by the group; registering again.", id);
                        Register(instances);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning("Renewing the instance registration failed: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            // the loop's own end: running tasks are cancelled and given the grace period, their items go back to the queue, the leases are released
            if (_loop is not null) await _loop.ConfigureAwait(false);
            if (_beat is not null) await _beat.ConfigureAwait(false);
        }
        finally
        {
            if (_instance is { } id)
            {
                try { new RunnerInstances(state).MarkGone(id); }
                catch (Exception ex) { _log.LogWarning("Marking the instance gone failed: {Message}", ex.Message); }
                lock (_gate) _instance = null;
            }

            if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
            _host = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
        _host = null;
        _stopping.Dispose();
    }
}
