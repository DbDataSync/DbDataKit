using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Quality;

/// <summary>When the package's task runs besides on request: on a schedule, and after another task's run (the manifest's <c>after-run</c> trigger).</summary>
/// <param name="Every">A schedule: the task runs this often. Null: only on request and after the runs below.</param>
/// <param name="AfterRuns">The tasks whose runs the task follows, each as <c>package/task</c> on the channel the package is served on (this package has no other task to follow). A run of one of them that succeeded queues one evaluation (a trigger fires on <c>succeeded</c> unless <see cref="Outcomes"/> says more).</param>
/// <param name="Outcomes">The outcomes of the followed runs that start an evaluation; null: <c>succeeded</c>.</param>
/// <param name="Dataset">The dataset the scheduled and triggered runs evaluate (identity or name); null: every dataset the host has checks for.</param>
public sealed record QualityTaskSettings(TimeSpan? Every = null, IReadOnlyList<string>? AfterRuns = null, IReadOnlyList<RunOutcome>? Outcomes = null, string? Dataset = null);

/// <summary>
/// The system package <c>quality-system</c> and its task <c>quality</c> (plan 23a FQ08, D5): the runner evaluates the checks of a dataset <b>in the host's process</b>, as the console's own tasks do (<c>console-system</c>): the task is the host's code and touches its state, and a package's task never does. It
/// <list type="bullet">
/// <item>runs <b>on request</b> (run now, <c>quality run</c> through the runner) and <b>on a schedule</b> (<see cref="QualityTaskSettings.Every"/>);</item>
/// <item>runs <b>after another task's run</b> through the manifest's <c>after-run</c> trigger (<see cref="QualityTaskSettings.AfterRuns"/>): the trigger engine queues one work item per finished run it follows, and the task evaluates the checks of the dataset it is set to (the input <c>dataset</c>), or of every dataset the host has;</item>
/// <item>writes <c>check_result</c> and <c>check_status</c> through <see cref="QualityStore"/> by <see cref="QualityRunService"/> (the block rules are the store's), the runner's run id as each result's run id.</item>
/// </list>
/// A work item is claimed by one runner with a lease, so two runners of a group never evaluate one request twice. The run ends <c>succeeded</c> when every check passed, <c>completed with failures</c> when some failed or could not run (a finding is not a failure of the task),
/// and <c>failed</c> when the task itself could not run (an unknown dataset).
/// <para>
/// <b>Opening the <c>quality</c> module.</b> The host opens <see cref="QualityModule.Module"/> (one copy per install: the console, Build, Sync, DbDataStore, a runner) with the modules it already has, and the serving layer opens <see cref="QualityModule.GroupModule"/> (one copy per group, shared by its instances).
/// This task needs the module <c>runs</c> as well (the run record) and the host's <see cref="IQualitySource"/>.
/// </para>
/// </summary>
public sealed class QualitySystemTasks(IQualitySource source, StateDatabase state, QualityStore store, EvaluationLimits? limits = null, string service = "runner", IQualityAlerts? alerts = null)
{
    public const string Package = "quality-system";
    public const string Task = "quality";
    public const string DatasetInput = "dataset";

    /// <summary>The manifest of the package, as the <c>package/1</c> YAML the runner's publishing takes.</summary>
    public static string Manifest(QualityTaskSettings? settings = null)
    {
        settings ??= new QualityTaskSettings();
        var triggers = new List<TaskTrigger>();
        foreach (var follow in settings.AfterRuns ?? [])
        {
            var slash = follow.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0 || slash == follow.Length - 1) throw new ArgumentException($"'{follow}' is not a task to follow: write it as package/task.", nameof(settings));
            triggers.Add(new TaskTrigger(TriggerKind.AfterRun, Task: follow[(slash + 1)..], Package: follow[..slash], On: settings.Outcomes is { Count: > 0 } on ? on : null));
        }

        var task = new PackageTask(Task, new TaskEntry(), settings.Every is { } every ? Schedule.Interval(every) : new Schedule(), triggers,
            [new TaskInput(DatasetInput, ProgressType.Text, Required: false, Default: settings.Dataset, Description: "A dataset's identity or name; empty: every dataset the host has checks for.")],
            [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var manifest = new PackageManifest(Package, "Evaluates the data quality checks of the datasets of this host and records the results.", new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"),
            new BuiltAgainst(RunnerContracts.Dotnet, null, RunnerContracts.Protocol), [task]);
        return PackageManifestWriter.Write(manifest);
    }

    /// <summary>Runs the task of <paramref name="request"/> in this process and records its run.</summary>
    public async Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct)
    {
        var runs = new RunStore(state, service);
        var runId = request.RunId ?? state.NewId();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        runs.Start(request.Task.ToString(), runId, package: request.Task.Package, packageVersion: request.PackageVersion, channel: request.Task.Channel, attempt: request.Attempt);
        TaskEnd end;
        try
        {
            ct.ThrowIfCancellationRequested();
            var dataset = request.Inputs.TryGetValue(DatasetInput, out var named) && !string.IsNullOrWhiteSpace(named) ? named : null;
            var report = await new QualityRunService(source, store, limits, alerts: alerts).RunAsync(dataset, runId.ToString("D"), ct).ConfigureAwait(false);
            var firstFailed = report.Checks.FirstOrDefault(c => c.Evaluation.Status != EvaluationStatus.Pass);
            end = report.AllPassed
                ? new TaskEnd(TaskEndState.Succeeded, Processed: report.Evaluated)
                : new TaskEnd(TaskEndState.CompletedWithFailures, Processed: report.Evaluated, FailedItems: report.Failed + report.Errors, FirstFailedItem: firstFailed is null ? null : $"{firstFailed.Check.Id} ({report.Datasets.First(d => d.Checks.Contains(firstFailed)).Dataset})");
        }
        catch (OperationCanceledException)
        {
            end = new TaskEnd(TaskEndState.Stopped);
        }
        catch (UnknownDatasetException ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, ex.Message));
        }
        catch (Exception ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, $"{ex.GetType().Name}: {ex.Message}"));
        }

        var finish = end.ToRunFinish();
        runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return new TaskRunResult(runId, end, true, null, false, clock.Elapsed, 0, 0, 0, 0, DbDataKit.Runner.Limits.LimitEnforcement.None, DbDataKit.Runner.Limits.LimitEnforcement.None, "none", 0, false, []);
    }
}

/// <summary>Hands the task of the package <c>quality-system</c> to <see cref="QualitySystemTasks"/> (in process) and every other task to the executor (a process per task). A host puts it around its executor like the console's own tasks' runner.</summary>
public sealed class QualityTaskRunner(QualitySystemTasks system, ITaskRunner others) : ITaskRunner
{
    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) =>
        request.Task.Package == QualitySystemTasks.Package && request.Task.Task == QualitySystemTasks.Task ? system.RunAsync(request, ct) : others.RunAsync(request, ct);
}
