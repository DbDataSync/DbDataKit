using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Server;

/// <summary>
/// The console's own tasks (plan 28 section 6a): the package <c>console-system</c> on the channel <c>system</c>, which the embedded runner serves whatever it is set to accept. They run <b>in the console's process</b>, not in a process of their
/// own: they are the console's code and touch its state, and a package's task never does.
/// <list type="bullet">
/// <item><c>state-retention</c>: the shared retention job (<see cref="RetentionJob"/>) over the console's state, on a schedule (<c>runner.retention_every_minutes</c>, an hour by default). It removes what the retention rules of the state's modules say is old.</item>
/// <item><c>state-backup</c> (only when <c>runner.backup_directory</c> is set): a snapshot of the console's state written to that folder every <c>runner.backup_every_hours</c> (a day), checked against its own manifest, the newest <c>runner.backup_keep</c> kept (<see cref="DbDataKit.State.Engines.Snapshots.StateBackupJob"/>); restoring is <c>dbdatakit state import --from &lt;folder&gt;</c> into an empty database.</item>
/// <item><c>evaluation</c>: a placeholder. Evaluation runs (plan 27 section 3.4) are tasks of this runner once plan 27 builds them; until then the task is listed, has no schedule, and a run says so and fails.</item>
/// </list>
/// A product's own housekeeping stays with the product: a server's state retention runs in that server.
/// </summary>
public sealed class ConsoleSystemTasks(StateDatabase state, RetentionSettings? retention, DbDataKit.State.Engines.Snapshots.StateBackupJob? backup = null)
{
    public const string Package = "console-system";
    public const string StateRetention = "state-retention";
    public const string Evaluation = "evaluation";
    public const string StateBackup = "state-backup";

    /// <summary>The manifest of the package, as the package/1 YAML the runner's publishing takes.</summary>
    public static string Manifest(TimeSpan retentionEvery, TimeSpan? backupEvery = null)
    {
        PackageTask Task(string name, Schedule schedule) => new(name, new TaskEntry(), schedule, [], [], [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var tasks = new List<PackageTask> { Task(StateRetention, Schedule.Interval(retentionEvery)), Task(Evaluation, new Schedule()) };
        if (backupEvery is { } every) tasks.Add(Task(StateBackup, Schedule.Interval(every)));
        var manifest = new PackageManifest(Package, "The console's own tasks, run inside the console's process.", new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"),
            new BuiltAgainst(RunnerContracts.Dotnet, null, RunnerContracts.Protocol),
            tasks);
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
                StateBackup => Backup(ct),
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

    /// <summary>Writes, verifies and prunes a snapshot of the console's state (plan 29b U03); fails with the reason when no folder is configured or the snapshot does not verify.</summary>
    private TaskEnd Backup(CancellationToken ct)
    {
        if (backup is null) return new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "No backup folder is set: name one as runner.backup_directory in the console's settings.yaml."));
        var result = backup.RunAsync(ct).GetAwaiter().GetResult();
        return new TaskEnd(TaskEndState.Succeeded, Processed: result.Rows);
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
