using DbDataBuild.Cli.Server;
using DbDataKit.Runner.Client;

namespace DbDataBuild.Cli.Runner;

/// <summary>What Build's schedule endpoints ask of the runner for a plan the runner owns (plan 28c E10, QE5): pause, resume and run now, and the runner's row of the task for the schedule list.</summary>
internal interface IRunnerTaskControl
{
    Task PauseAsync(string package, string task, CancellationToken ct);

    Task ResumeAsync(string package, string task, CancellationToken ct);

    /// <summary>The id of the run the runner queued, or null when one is queued or running already.</summary>
    Task<string?> RunNowAsync(string package, string task, CancellationToken ct);

    Task<ClientTaskStatus?> StatusAsync(string package, string task, CancellationToken ct);

    /// <summary>Queues a manual run of the task for a person (plan 28c E21): <paramref name="inputs"/> are the task's inputs, <paramref name="onBehalfOf"/> the person as the tracking tables should name them. The id of the run, or null when one is queued or running already.</summary>
    Task<string?> RunForAsync(string package, string task, IReadOnlyDictionary<string, string> inputs, string onBehalfOf, CancellationToken ct) => throw new NotSupportedException("This runner control cannot start a run for a person.");
}

/// <summary>The runner a <see cref="ScheduleRunnerLink"/> names, spoken to through the paired client; pause, resume and run need the operator role of the pairing (a publisher's pairing is refused by the runner, and the refusal is what the caller is told).</summary>
internal sealed class ClientRunnerTaskControl(RunnerPublishClient client, ScheduleRunnerLink link) : IRunnerTaskControl
{
    public Task PauseAsync(string package, string task, CancellationToken ct) => client.PauseAsync(link.Server, null, package, link.Channel, task, null, ct);

    public Task ResumeAsync(string package, string task, CancellationToken ct) => client.ResumeAsync(link.Server, null, package, link.Channel, task, null, ct);

    public Task<string?> RunNowAsync(string package, string task, CancellationToken ct) => client.RunNowAsync(link.Server, null, package, link.Channel, task, null, ct);

    public Task<string?> RunForAsync(string package, string task, IReadOnlyDictionary<string, string> inputs, string onBehalfOf, CancellationToken ct) => client.RunNowAsync(link.Server, null, package, link.Channel, task, inputs, onBehalfOf, null, ct);

    public Task<ClientTaskStatus?> StatusAsync(string package, string task, CancellationToken ct) => client.TaskStatusAsync(link.Server, null, package, link.Channel, task, null, ct);

    /// <summary>The client of a link, from the pairings kept in its client directory (or the default one).</summary>
    public static ClientRunnerTaskControl For(ScheduleRunnerLink link, Func<string, string?> environment) =>
        new(new RunnerPublishClient(new RunnerClientStore(link.ClientDirectory ?? RunnerClientStore.DefaultDirectory(environment)), environment), link);
}
