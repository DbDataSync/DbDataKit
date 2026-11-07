using DbDataKit.Runner;
using DbDataKit.Runner.Connections;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Limits;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Runner.Staging;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataKit.Runner;

/// <summary>
/// The runner engine inside a host process (plan 28a R10, R20): <c>dbdatarunner</c> wraps it as a hosted service, the console hosts a copy of it. At start it finds the task host, listens on the socket tasks talk to (in <c>run/</c> in the server's directory, mode 0700), registers this instance in the
/// <c>runner</c> module with what the machine can enforce (<see cref="LimitCapabilities"/>), marks the runs a dead process left as interrupted, and starts the scheduler loop on the materialization cache under <c>cache/</c>. It
/// renews the registration every <see cref="RunnerHostSettings.InstanceHeartbeat"/> (and registers again if the group marked it gone). At stop it ends the loop with its grace period (running tasks are cancelled, their items go back to
/// the queue), marks the instance gone and closes the socket, so a stop leaves no instance live and no run running.
/// </summary>
/// <summary>The state is SQLite, which one runner at a time may use, and another instance is live on it (plan 28a R11).</summary>
public sealed class SecondInstanceException(string message) : InvalidOperationException(message);

public class RunnerHost(RunnerHostSettings settings, StateDatabase state, ILogger? logger = null) : IAsyncDisposable
{
    private readonly ILogger _log = logger ?? NullLogger.Instance;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private RunnerProtocolHost? _host;
    private Task? _loop;
    private Task? _beat;
    private Task? _staging;
    private Guid? _instance;
    private RunnerControl? _control;
    private RunnerScheduler? _scheduler;
    private volatile bool _silent;
    private IReadOnlyDictionary<string, string> _capabilities = new Dictionary<string, string>();
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    /// <summary>The control over the tasks; null until the engine has started.</summary>
    public RunnerControl? Control => _control;

    /// <summary>This process's row in the group's instances; null until the engine has started and after it has stopped.</summary>
    public Guid? InstanceId => _instance;

    /// <summary>What this machine reports about itself (memory and CPU limits, the operating system, the network).</summary>
    public IReadOnlyDictionary<string, string> Capabilities => _capabilities;

    /// <summary>How long an instance may be silent before it is not live to the group.</summary>
    public TimeSpan Lease => settings.InstanceLease;

    /// <summary>
    /// For a test or a diagnosis: while true this process renews neither its instance row nor the leases of its work items, as a process that was stopped or cut off from the database would. Its tasks keep running; the group takes the items
    /// once the lease runs out, and a process that is let go again finds out it lost them.
    /// </summary>
    public bool SuspendHeartbeats
    {
        get => _silent;
        set
        {
            _silent = value;
            if (_scheduler is not null) _scheduler.SuspendHeartbeats = value;
        }
    }

    /// <summary>The runner group this instance belongs to.</summary>
    public string Group => settings.Group;

    /// <summary>The name this runner's scheduler claims work items under; the group view counts an instance's running items by it.</summary>
    public string WorkerId => _workerId;

    /// <summary>The capability rows that are about the process, not the machine: the group view does not show them as capabilities.</summary>
    public static readonly IReadOnlySet<string> InternalCapabilities = new HashSet<string>(StringComparer.Ordinal) { "process_id", "worker_id" };

    public string TaskHostPath { get; private set; } = "";

    /// <summary>The channels this runner serves.</summary>
    public IReadOnlyCollection<string> Channels => settings.Channels;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // a program that cannot start a task is not started: the missing file is named now, not at the first run
        TaskHostPath = RunnerTaskHost.Require(settings.TaskHostPath);
        Directory.CreateDirectory(settings.CacheDirectory);
        Directory.CreateDirectory(settings.ScratchDirectory);

        var instances = new RunnerInstances(state);
        var others = LiveOthers(instances);
        if (!RunnerNames.IsGroup(state) && others.Count > 0)
        {
            var first = others[0];
            throw new SecondInstanceException(
                $"Another runner instance ({first.Instance.Id}, host {first.Instance.Host}, started {first.Instance.StartedAt:u}, last heartbeat {first.Instance.HeartbeatAt:u}) is running against this state database, which is SQLite: one runner may use it. " +
                "Stop that one, or run a group of runners against a SQL Server or PostgreSQL state (DBDATARUNNER_STATE_ENGINE and DBDATARUNNER_STATE_CONNECTION).");
        }

        _host = await RunnerProtocolHost.StartAsync(new RunnerProtocolHostOptions { Directory = settings.SocketDirectory }, cancellationToken).ConfigureAwait(false);
        var payloads = new PayloadStore(state);
        var publisher = new DefinitionPublisher(state, settings.Kinds ?? RunnerPackages.Kinds(), payloads, "runner");

        // connection slots (plan 28c E02): the bindings are the publisher's, the connections a directory of connection/1 files, the secrets the runner's own providers; every secret the resolver reveals is registered with the redactor the run log uses
        var redactor = new Redactor();
        var slots = new RunnerSlotResolver(publisher, settings.Connections ?? new DirectoryConnectionCatalog(settings.ConnectionsDirectory), settings.Secrets ?? RunnerHostSettings.DefaultSecrets(), redactor);
        var executor = new TaskExecutor(state, _host, new TaskExecutorOptions
        {
            DataDirectory = settings.Directory,
            TaskHostPath = TaskHostPath,
            Service = RunnerNames.EngineService,
            Redactor = redactor,
            Slots = slots,
            Logger = _log,
        });

        // the runs a process that died left behind: those of a SQLite state are this runner's alone; a group's are only marked when no instance of it is live (another one may be running them)
        if (!RunnerNames.IsGroup(state) || instances.ListLive(settings.InstanceLease).Count == 0)
        {
            var interrupted = executor.InterruptStaleRuns(state.Time.GetUtcNow());
            if (interrupted > 0) _log.LogWarning("{Count} run(s) of an earlier process were still marked running and are now marked interrupted.", interrupted);
        }

        _capabilities = LimitCapabilities.Detect();
        Register(instances);

        var cache = new ArtifactCache(settings.CacheDirectory, new PayloadArtifactSource(payloads));
        var scheduler = new RunnerScheduler(state, settings.WrapRunner?.Invoke(executor) ?? executor, cache,
            new DefinitionVersionSource(new DefinitionStore(state), publisher),
            new RunnerSchedulerOptions
            {
                Channels = settings.Channels,
                Service = RunnerNames.EngineService,
                Tick = settings.Tick,
                MaxConcurrentRuns = settings.MaxConcurrentRuns,
                CancellationGrace = settings.CancellationGrace,
                Heartbeat = settings.InstanceHeartbeat,
                Lease = settings.InstanceLease,
                WorkerId = _workerId,
                Instance = () => _instance,
                Logger = _log,
                Slots = slots,
            });
        _scheduler = scheduler;
        scheduler.SuspendHeartbeats = _silent;
        _control = new RunnerControl(state, scheduler, settings.ServiceId);
        _loop = Task.Run(() => scheduler.RunAsync(_stopping.Token));
        _beat = Task.Run(() => HeartbeatAsync(instances, _stopping.Token));

        // staging (plan 28a R17): every staged version of a package is prepared here and reported; whichever instance sees every live one ready activates it (R18: after the runs in flight, with the progress migration)
        var activator = new PackageActivator(state, publisher, new ActivationOptions { MaxWait = settings.ActivationMaxWait, Service = RunnerNames.EngineService, Logger = _log });
        var follower = new StagingFollower(state, publisher, cache, activator, new StagingOptions { Instance = () => _instance, Lease = settings.InstanceLease, Poll = settings.Tick, Contracts = settings.StagingContracts ?? RunnerContracts.Check, Logger = _log });
        if (settings.Staging) _staging = Task.Run(() => follower.RunAsync(_stopping.Token));
        _log.LogInformation("Runner {Instance} serving the channels {Channels} (task host {TaskHost}).", _instance, string.Join(", ", settings.Channels), TaskHostPath);
    }

    /// <summary>The live instances of the group other than this process: a row inside its lease whose process, when it is on this machine, still runs (a crashed process's row stays inside its lease for a while).</summary>
    private List<LiveInstance> LiveOthers(RunnerInstances instances) =>
        [.. instances.LiveInstances(settings.InstanceLease).Where(i => i.Instance.Id != _instance && !(i.Instance.Host == Environment.MachineName && !ProcessRuns(i)))];

    private static bool ProcessRuns(LiveInstance instance)
    {
        if (!instance.Capabilities.TryGetValue("process_id", out var text) || !int.TryParse(text, out var pid)) return true;
        try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>
    /// One row per process: registers this process, reports what the machine can enforce (also as read-only labels: <c>network:outside</c>, <c>os:linux</c>, <c>memory_limit:watched</c>), and clears what an earlier process of this machine
    /// left (rows that are gone, lapsed or whose process is dead), handing its administrator labels to the new row.
    /// </summary>
    private void Register(RunnerInstances instances)
    {
        var id = instances.Register(settings.ServiceId, Environment.MachineName, settings.Version ?? typeof(RunnerHost).Assembly.GetName().Version?.ToString() ?? "0.0.0");
        var all = new Dictionary<string, string>(_capabilities, StringComparer.Ordinal)
        {
            ["process_id"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["worker_id"] = _workerId,
        };
        instances.ReplaceCapabilities(id, all);
        instances.ReplaceReportedLabels(id, RunnerLabels.FromCapabilities(_capabilities));
        var inherited = new List<string>();
        foreach (var dead in instances.LiveInstances(settings.InstanceLease).Where(i => i.Instance.Id != id && i.Instance.Host == Environment.MachineName && !ProcessRuns(i)))
        {
            inherited.AddRange(instances.Labels(dead.Instance.Id).Where(l => l.Source == InstanceLabel.Admin).Select(l => l.Label));
            instances.Remove(dead.Instance.Id);
        }

        inherited.AddRange(instances.RemoveLapsed(settings.ServiceId, Environment.MachineName, id, settings.InstanceLease, anyHost: !RunnerNames.IsGroup(state)));
        foreach (var label in inherited.Distinct(StringComparer.Ordinal)) instances.SetLabel(id, label, InstanceLabel.Admin);
        lock (_gate) _instance = id;
    }

    private async Task HeartbeatAsync(RunnerInstances instances, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(settings.InstanceHeartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (_silent) continue;
                try
                {
                    if (_instance is { } id)
                    {
                        var before = instances.Get(id);
                        var silent = before is null ? TimeSpan.Zero : state.Time.GetUtcNow() - before.HeartbeatAt;
                        if (silent > settings.InstanceLease)
                            _log.LogWarning("This runner was silent for {Silent} (the lease is {Lease}): the group may have taken its work items; any item it lost is cancelled here and its result is not recorded.", silent, settings.InstanceLease);
                        if (before is null || !instances.Heartbeat(id))
                        {
                            _log.LogWarning("The instance {Instance} was removed or marked gone by the group; registering again.", id);
                            Register(instances);
                        }
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
            if (_staging is not null) await _staging.ConfigureAwait(false);
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
