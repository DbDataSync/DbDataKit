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

    /// <summary>Starts a runner whose instance id is <paramref name="id"/>, serving the channels <c>dev</c> and <c>prod</c>.</summary>
    public static async Task<PairedRunner> StartAsync(string id = "runner-1")
    {
        var directory = Directory.CreateTempSubdirectory("ddk-paired-runner").FullName;
        var state = StateOpener.Open(new StateSettings(), Path.Combine(directory, "state.db"), RunnerNames.EngineModules, new StateScope(Group: RunnerNames.DefaultGroup, Service: RunnerNames.ServiceId));
        var host = new RunnerHost(new RunnerHostSettings(Path.Combine(directory, "runner"))
        {
            Channels = ["dev", "prod"],
            Tick = TimeSpan.FromMilliseconds(200),
            CancellationGrace = TimeSpan.FromSeconds(5),
            TaskHostPath = typeof(RunnerHost).Assembly.Location,
            WrapRunner = _ => new NoRun(),
        }, state);
        await host.StartAsync(CancellationToken.None);
        var publishing = new RunnerPublishing(state);
        var serverDirectory = Directory.CreateDirectory(Path.Combine(directory, "server")).FullName;
        var server = await PairableServer.StartAsync(directory: serverDirectory, product: ServerProducts.Runner, instanceId: id, map: (app, _, _) =>
        {
            RunnerApi.Map(app, state, () => host, id, new RunnerApiOptions("/api/v1", RunnerCallers.TokenResolver, ServerProducts.Runner, "9.9.9"));
            RunnerPublishApi.Map(app, publishing);
            RunnerConnectionCopyApi.Map(app, state, () => host, "/api/v1", RunnerCallers.TokenResolver);
            RunnerQualityApi.Map(app, state, "/api/v1", RunnerCallers.TokenResolver);
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
