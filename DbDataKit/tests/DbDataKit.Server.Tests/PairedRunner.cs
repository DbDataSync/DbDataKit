using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Execution;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// A runner a console pairs with, in this process (plan 35 I2: the console embeds none): a <see cref="PairableServer"/> of the product <c>dbdatarunner</c> serving the real runner engine and its management, publish and connection-copy
/// calls over a state of its own (SQLite), with the token check of a real server. What it runs is <see cref="Tasks"/> (nothing, by default). The runner's own server is tested in <c>DbDataRunner.Tests</c>.
/// </summary>
internal sealed class PairedRunner : IAsyncDisposable
{
    private sealed class NoRun : ITaskRunner
    {
        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) => throw new InvalidOperationException("nothing is scheduled in this test");
    }

    private readonly string _directory;

    private PairedRunner(string directory, PairableServer server, RunnerHost host, StateDatabase state, RunnerPublishing publishing) => (_directory, Server, Host, State, Publishing) = (directory, server, host, state, publishing);

    public PairableServer Server { get; }

    public RunnerHost Host { get; }

    /// <summary>The runner's own state.</summary>
    public StateDatabase State { get; }

    public RunnerPublishing Publishing { get; }

    /// <summary>The runner's own directory of connection files (<c>connection/1</c>), the first place its slot resolver looks for the connection a binding names.</summary>
    public string ConnectionsDirectory => Path.Combine(_directory, "runner", "connections");

    /// <summary>
    /// Starts a runner whose instance id is <paramref name="id"/>, serving the channels <c>dev</c> and <c>prod</c>. <paramref name="tasks"/>, when given, runs every task in this process over the runner's state (<see cref="InProcessTasks"/>);
    /// otherwise a run fails with "nothing is scheduled in this test".
    /// </summary>
    public static async Task<PairedRunner> StartAsync(string id = "runner-1", Func<StateDatabase, ITaskRunner>? tasks = null)
    {
        var directory = Directory.CreateTempSubdirectory("ddk-paired-runner").FullName;
        var state = StateOpener.Open(new StateSettings(), Path.Combine(directory, "state.db"), [.. RunnerNames.EngineModules, TrustModule.Module], new StateScope(Group: RunnerNames.DefaultGroup, Service: RunnerNames.ServiceId));
        var host = new RunnerHost(new RunnerHostSettings(Path.Combine(directory, "runner"))
        {
            Channels = ["dev", "prod"],
            Tick = TimeSpan.FromMilliseconds(200),
            CancellationGrace = TimeSpan.FromSeconds(5),
            TaskHostPath = typeof(RunnerHost).Assembly.Location,
            WrapRunner = _ => tasks?.Invoke(state) ?? new NoRun(),
        }, state);
        await host.StartAsync(CancellationToken.None);
        var publishing = new RunnerPublishing(state);
        var serverDirectory = Directory.CreateDirectory(Path.Combine(directory, "server")).FullName;
        var server = await PairableServer.StartAsync(directory: serverDirectory, product: ServerProducts.Runner, instanceId: id, map: (app, _, _) =>
        {
            RunnerApi.Map(app, state, () => host, id, new RunnerApiOptions("/api/v1", RunnerCallers.TokenResolver, ServerProducts.Runner, "9.9.9"));
            RunnerPublishApi.Map(app, publishing);
            RunnerConnectionCopyApi.Map(app, state, () => host, "/api/v1", RunnerCallers.TokenResolver);
            RunnerBindingTestApi.Map(app, state, () => host.Slots, "/api/v1", RunnerCallers.TokenResolver);
            RunnerQualityApi.Map(app, state, "/api/v1", RunnerCallers.TokenResolver, engine: () => host);
        });
        return new PairedRunner(directory, server, host, state, publishing);
    }

    /// <summary>Pairs the console of <paramref name="app"/> with this runner, as an administrator does on the Servers page; the console may grant what its administrators hold.</summary>
    public Task PairAsync(WebApplication app, string name = "Runner one") =>
        app.Services.GetRequiredService<ConsoleInstances>().PairAsync(Server.Url, Server.NewCodeWith(ServerRole.Admin | ServerRole.Operator | ServerRole.Publisher), name, default);

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Server.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   // the artifact store's files are read-only on Windows
    }
}

/// <summary>
/// A task of a package run in this process, the way the runner's executor records one: the run begins with the version it is of, the steps <paramref name="steps"/> gives for the request are recorded (a step's name and its result as
/// JSON; none for a task it does not know), and the run succeeds. <see cref="Requests"/> keeps each request, so a test can say who asked, with which inputs and of which version.
/// </summary>
internal sealed class InProcessTasks(StateDatabase state, Func<TaskRunRequest, IReadOnlyList<(string Step, string Result)>> steps, Action<TaskRunRequest, Guid>? answer = null) : ITaskRunner
{
    public System.Collections.Concurrent.ConcurrentQueue<TaskRunRequest> Requests { get; } = new();

    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default)
    {
        Requests.Enqueue(request);
        var runs = new RunStore(state, RunnerNames.EngineService);
        var runId = request.RunId ?? state.NewId();
        runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
        var store = new StepStore(state, new ProgressKey(request.Task.Package, request.Task.Channel, request.Task.Task));
        var i = 0;
        foreach (var (step, result) in steps(request))
        {
            var key = $"{step}-{i++}";
            store.Begin(request.Chain, step, key, request.Attempt);
            store.CompleteValue(request.Chain, step, key, request.Attempt, result);
        }
        answer?.Invoke(request, runId);
        var end = new TaskEnd(TaskEndState.Succeeded, Processed: 1);
        var finish = end.ToRunFinish();
        runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return Task.FromResult(new TaskRunResult(runId, end, true, null, false, TimeSpan.Zero, 0, 0, 0, 0, DbDataKit.Runner.Limits.LimitEnforcement.None, DbDataKit.Runner.Limits.LimitEnforcement.None, "none", 0, false, []));
    }
}
