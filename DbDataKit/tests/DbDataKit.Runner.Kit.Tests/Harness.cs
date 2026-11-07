using System.Text.Json;
using DbDataKit.Monitoring;
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

    public Harness(string engine)
    {
        Database = _engines.Open(engine);
        World = World.Begin();
    }

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
        JobRunner.RunAsync<TJob>(Environment(hooks, steps: steps), chain, attempt, input, ct);

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

    /// <summary>A crash after the body of the named streaming step returned and before its payload was committed: the write was interrupted, an incomplete payload is left.</summary>
    public static RunnerHooks CrashBeforeCommit(string step) => new()
    {
        BeforePayloadCommit = (name, _, _) => name == step ? throw new SimulatedCrashException($"the process died while '{step}' was writing") : Task.CompletedTask,
    };

    public static T Json<T>(string? json) => JsonSerializer.Deserialize<T>(json ?? "null", JsonSerializerOptions.Web)!;

    public void Dispose() => _engines.Dispose();
}
