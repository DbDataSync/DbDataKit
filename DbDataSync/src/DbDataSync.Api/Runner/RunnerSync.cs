using DbDataKit.Runner.Client;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>A move of a replication that was refused or could not be finished; the message says what was found and what is as it was.</summary>
public sealed class OwnershipException(string message) : InvalidOperationException(message);

/// <summary>What the Sync API asks of the runner about a replication's task (plan 28c E12): who schedules it, run now with inputs, pause and resume, and the runner's row of it.</summary>
public interface IRunnerSync
{
    Task<ClientTaskSchedule> ScheduleOfAsync(string task, CancellationToken ct);

    Task SetOwnerAsync(string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct);

    /// <summary>The id of the run the runner queued, or null when one is queued or running already.</summary>
    Task<string?> RunNowAsync(string task, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct);

    Task PauseAsync(string task, CancellationToken ct);

    Task ResumeAsync(string task, CancellationToken ct);

    Task<ClientTaskStatus?> StatusAsync(string task, CancellationToken ct);

    /// <summary>The items of the task's checkpoint <paramref name="name"/> as the runner holds them (the drain's positions are the checkpoint <c>watermark</c>, one item per source table).</summary>
    Task<IReadOnlyList<ClientCheckpointItem>> CheckpointAsync(string task, string name, CancellationToken ct);

    /// <summary>Puts an item in the task's own queue, which a drain that is up takes in its next sweep (the runner starts one drain of a task at a time).</summary>
    Task EnqueueWorkAsync(string task, string kind, string subject, string part, string? payload, CancellationToken ct);
}

/// <summary>The runner a <see cref="RunnerLink"/> names, spoken to through the paired client (the owner, run, pause and resume calls need the operator role of the pairing).</summary>
public sealed class ClientRunnerSync(RunnerPublishClient client, RunnerLink link) : IRunnerSync, IRunnerRuns
{
    public Task<IReadOnlyList<ClientRun>> RunsAsync(string? task, int limit, DateTimeOffset? before, string? outcome, CancellationToken ct) => client.RunsAsync(link.Server, null, link.Package, link.Channel, task, limit, before, outcome, null, ct);

    public Task<ClientRun?> RunAsync(Guid id, CancellationToken ct) => client.RunAsync(link.Server, null, id, null, ct);

    public Task<IReadOnlyList<ClientLogLine>?> LogsAsync(Guid id, long? after, int limit, CancellationToken ct) => client.RunLogsAsync(link.Server, null, id, after, limit, null, ct);

    public Task<IReadOnlyList<ClientStep>> StepsAsync(string task, string step, int limit, DateTimeOffset? before, string? key, CancellationToken ct) => client.StepsAsync(link.Server, null, link.Package, link.Channel, task, step, limit, before, key, null, ct);

    public Task<ClientTaskSchedule> ScheduleOfAsync(string task, CancellationToken ct) => client.ScheduleOfAsync(link.Server, null, link.Package, link.Channel, task, null, ct);

    public Task SetOwnerAsync(string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct) => client.SetOwnerAsync(link.Server, null, link.Package, link.Channel, task, owner, reason, seed, null, ct);

    public Task<string?> RunNowAsync(string task, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct) => client.RunNowAsync(link.Server, null, link.Package, link.Channel, task, inputs, null, ct);

    public Task PauseAsync(string task, CancellationToken ct) => client.PauseAsync(link.Server, null, link.Package, link.Channel, task, null, ct);

    public Task ResumeAsync(string task, CancellationToken ct) => client.ResumeAsync(link.Server, null, link.Package, link.Channel, task, null, ct);

    public Task<ClientTaskStatus?> StatusAsync(string task, CancellationToken ct) => client.TaskStatusAsync(link.Server, null, link.Package, link.Channel, task, null, ct);

    public Task<IReadOnlyList<ClientCheckpointItem>> CheckpointAsync(string task, string name, CancellationToken ct) => client.CheckpointAsync(link.Server, null, link.Package, link.Channel, task, name, null, ct);

    public async Task EnqueueWorkAsync(string task, string kind, string subject, string part, string? payload, CancellationToken ct) =>
        await client.EnqueueWorkAsync(link.Server, null, link.Package, link.Channel, task, kind, subject, part, payload, null, ct);

    public static ClientRunnerSync For(RunnerLink link, Func<string, string?> environment) =>
        new(new RunnerPublishClient(new RunnerClientStore(link.ClientDirectory ?? RunnerClientStore.DefaultDirectory(environment)), environment), link);
}

/// <summary>The runner of the file's link, opened when it is asked for (a link may be set while the API runs); null while no link is set.</summary>
public sealed class RunnerAccess(RunnerOwnerFile file, Func<RunnerLink, IRunnerSync> open)
{
    public RunnerOwnerFile File => file;

    public RunnerLink? Link => file.Read().Link;

    public IRunnerSync? Open() => Link is { } link ? open(link) : null;

    /// <summary>What the runner of the link says of the runs of a replication (plan 28c E13), or null while no link is set or the opened runner cannot be asked (a test's fake that only moves owners).</summary>
    public IRunnerRuns? OpenRuns() => Open() as IRunnerRuns;

    public IRunnerSync Require() => Open() ?? throw new OwnershipException($"No runner is linked: set the runner (server, channel and package) before a replication is moved ({file.Path}).");

    public static string TaskOf(string replication) => RunnerTaskNames.Of(replication);
}
