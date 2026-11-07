using System.Text.Json;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>One test's state database on an engine, a <see cref="World"/>, and a way to run jobs against them as attempts of a chain.</summary>
public sealed class Harness : IDisposable
{
    public const string Package = "kit-tests";
    public const string Channel = "prod";
    public const string Chain = "0197f0e0-0000-7000-8000-00000000c0de";

    private readonly KitEngines _engines = new();

    private readonly RunnerProtocolHost? _host;

    /// <summary>
    /// <paramref name="remote"/>: every job runs through the runner protocol on a real Unix socket (a <see cref="RunnerProtocolHost"/> over this database and <see cref="RemoteJobHost"/> in the same process, which is the
    /// task side), with the same assertions as in-process. On Windows the host listens on a named pipe instead, so the same suite tests that path there.
    /// </summary>
    public Harness(string engine, bool remote = false, TimeProvider? time = null)
    {
        Database = _engines.Open(engine, time);
        World = World.Begin();
        if (remote)
        {
            _host = RunnerProtocolHost.StartAsync().GetAwaiter().GetResult();
        }
    }

    public bool Remote => _host is not null;

    /// <summary>What the last remote run reported one way (counts, status, lineage, chunk statistics), read before its token was forgotten.</summary>
    public RunTelemetry? LastTelemetry { get; private set; }

    public RunnerProtocolHost Host => _host ?? throw new InvalidOperationException("This harness runs in-process.");

    public StateDatabase Database { get; }

    public World World { get; }

    public RunStore Runs => new(Database, "runner");

    public ProgressStore ProgressOf(string task) => new(Database, new ProgressKey(Package, Channel, task));

    public StepStore StepsOf(string task, StepOptions? options = null) => new(Database, new ProgressKey(Package, Channel, task), options);

    public UnitStore UnitsOf(string task) => new(Database, new ProgressKey(Package, Channel, task));

    public RunnerEnvironment Environment(RunnerHooks? hooks = null, int? maxParallelism = null, StepOptions? steps = null) =>
        new(Database, Package, Channel)
        {
            SlotOpener = (_, _, _) => Task.FromResult<object>(new MemoryWarehouse(World)),
            Hooks = hooks,
            MaxParallelism = maxParallelism,
            Steps = steps,
        };

    /// <summary>One attempt of the chain (the same chain by default, so a retry shares the steps and units of the first).</summary>
    public Task<RunResult> Run<TJob>(object? input = null, int attempt = 1, RunnerHooks? hooks = null, string chain = Chain, StepOptions? steps = null, CancellationToken ct = default)
        where TJob : RunnerJobBase, new() =>
        _host is null ? JobRunner.RunAsync<TJob>(Environment(hooks, steps: steps), chain, attempt, input, ct) : RunRemote<TJob>(input, attempt, hooks, chain, steps, ct);

    /// <summary>The token of the last grant, so a test can ask the runner to cancel that run.</summary>
    public string? LastToken { get; private set; }

    /// <summary>What the runner does before a task starts: grants a token for a run of the task <paramref name="task"/> of this harness's package and channel.</summary>
    public string Grant(string task, object? input = null, int attempt = 1, string chain = Chain, StepOptions? steps = null, TimeSpan? grace = null, Guid? runId = null, RunnerProtocolHost? host = null)
    {
        var start = new TaskStart(new TaskRef(Package, Channel, task), runId ?? Database.NewId(), attempt, "1.0.0", InputBinder.ToStrings(input), new Dictionary<string, string>(), null, grace ?? TimeSpan.FromSeconds(30));
        var token = (host ?? Host).Register(new RunGrant(start, chain, Environment(null, steps: steps)));
        lock (_chains) _chains[token] = chain;
        return LastToken = token;
    }

    private readonly Dictionary<string, string> _chains = [];

    /// <summary>The attempt chain a token was granted for.</summary>
    public string ChainOfToken(string token)
    {
        lock (_chains) return _chains[token];
    }

    /// <summary>What the runner does for a task: grants a token for the run, hands the task its endpoint and token, and withdraws the token when the task's process is gone.</summary>
    public async Task<RunResult> RunRemote<TJob>(object? input, int attempt, RunnerHooks? hooks, string chain, StepOptions? steps, CancellationToken ct, TimeSpan? grace = null, Guid? runId = null)
        where TJob : RunnerJobBase, new()
    {
        var token = Grant(JobRunner.TaskNameOf(typeof(TJob)), input, attempt, chain, steps, grace, runId);
        try
        {
            return await RemoteJobHost.RunAsync<TJob>(Host.Endpoint, token, ct, new RemoteRunOptions
            {
                Hooks = hooks,
                SlotOpener = (_, _, _) => Task.FromResult<object>(new MemoryWarehouse(World)),
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
            }).ConfigureAwait(false);
        }
        finally
        {
            LastTelemetry = Host.TelemetryOf(token);
            Host.Unregister(token);
        }
    }

    /// <summary>What the serving layer does: stores a webhook body as a payload owned by an inbox row.</summary>
    public Guid Receive(string task, string source, byte[] body)
    {
        var id = Database.NewId();
        Guid payload;
        using (var writer = new PayloadStore(Database).OpenWrite(new PayloadOptions("application/json", ProgressModule.Name, id.ToString("D"))))
        {
            payload = writer.Id;
            writer.Write(body);
            writer.Complete();
        }

        ProgressOf(task).Receive(source, true, id, payload);
        return id;
    }

    public Guid Receive(string task, string source, object body) => Receive(task, source, JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web));

    public StoredRun StoredRun(Guid id) => Runs.Get(id) ?? throw new InvalidOperationException($"no run {id}");

    /// <summary>A crash after the body of the named step returned and before its result is stored.</summary>
    public static RunnerHooks CrashAfterStep(string step) => new()
    {
        AfterStepBody = (name, _, _) => name == step ? throw new SimulatedCrashException($"the process died after the body of '{step}'") : Task.CompletedTask,
    };

    public static T Json<T>(string? json) => JsonSerializer.Deserialize<T>(json ?? "null", JsonSerializerOptions.Web)!;

    public void Dispose()
    {
        _host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engines.Dispose();
    }
}
