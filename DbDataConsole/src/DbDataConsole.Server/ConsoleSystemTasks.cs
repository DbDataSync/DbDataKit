using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataConsole.Server;

/// <summary>
/// The console's own tasks (plan 28 section 6a): the package <c>console-system</c> on the channel <c>system</c>, which the embedded runner serves whatever it is set to accept. They run <b>in the console's process</b>, not in a process of their
/// own: they are the console's code and touch its state, and a package's task never does.
/// <list type="bullet">
/// <item><c>state-retention</c>: the shared retention job (<see cref="RetentionJob"/>) over the console's state, on a schedule (<c>runner.retention_every_minutes</c>, an hour by default). It removes what the retention rules of the state's modules say is old.</item>
/// <item><c>evaluation</c>: a placeholder. Evaluation runs (plan 27 section 3.4) are tasks of this runner once plan 27 builds them; until then the task is listed, has no schedule, and a run says so and fails.</item>
/// </list>
/// A product's own housekeeping stays with the product: a server's state retention runs in that server.
/// </summary>
public sealed class ConsoleSystemTasks(StateDatabase state, RetentionSettings? retention)
{
    public const string Package = "console-system";
    public const string StateRetention = "state-retention";
    public const string Evaluation = "evaluation";

    /// <summary>The manifest of the package, as the package/1 YAML the runner's publishing takes.</summary>
    public static string Manifest(TimeSpan retentionEvery)
    {
        PackageTask Task(string name, Schedule schedule) => new(name, new TaskEntry(), schedule, [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var manifest = new PackageManifest(Package, "The console's own tasks, run inside the console's process.", new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"),
            new BuiltAgainst(RunnerContracts.Dotnet, null, RunnerContracts.Protocol),
            [Task(StateRetention, Schedule.Interval(retentionEvery)), Task(Evaluation, new Schedule())]);
        return PackageManifestWriter.Write(manifest);
    }

    /// <summary>Runs the system task of <paramref name="request"/> in this process and records its run.</summary>
    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct)
    {
        var runs = new RunStore(state, RunnerNames.EngineService);
        var runId = request.RunId ?? state.NewId();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
        TaskEnd end;
        try
        {
            ct.ThrowIfCancellationRequested();
            end = request.Task.Task switch
            {
                StateRetention => Retain(),
                Evaluation => new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "Evaluation runs are not built yet (plan 27); this task is a placeholder for them.")),
                var other => new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, $"The console has no system task '{other}'.")),
            };
        }
        catch (OperationCanceledException)
        {
            end = new TaskEnd(TaskEndState.Stopped);
        }
        catch (Exception ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, $"{ex.GetType().Name}: {ex.Message}"));
        }

        var finish = end.ToRunFinish();
        runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return Task.FromResult(new TaskRunResult(runId, end, true, null, false, clock.Elapsed, 0, 0, 0, 0, DbDataKit.Runner.Limits.LimitEnforcement.None, DbDataKit.Runner.Limits.LimitEnforcement.None, "none", 0, false, []));
    }

    private TaskEnd Retain()
    {
        var result = new RetentionJob(state, retention).Run();
        return new TaskEnd(TaskEndState.Succeeded, Processed: result.Removed.Values.Sum());
    }
}

/// <summary>Hands the console's own system tasks to <see cref="ConsoleSystemTasks"/> (in process) and every other task to the executor (a process per task).</summary>
public sealed class ConsoleTaskRunner(ConsoleSystemTasks system, ITaskRunner others) : ITaskRunner
{
    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) =>
        request.Task.Package == ConsoleSystemTasks.Package && request.Task.Channel == ConsoleRunnerSettings.SystemChannel
            ? system.RunAsync(request, ct)
            : others.RunAsync(request, ct);
}
