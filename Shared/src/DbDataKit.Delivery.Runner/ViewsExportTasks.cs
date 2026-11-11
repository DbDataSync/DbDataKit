using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Delivery;

/// <summary>How often the task looks for due subscriptions (a subscription is at most hourly, so a minute is fine enough: a subscription goes out within a minute of its time).</summary>
public sealed record ViewsExportSettings(TimeSpan? Every = null);

/// <summary>
/// The system package <c>views-export</c> and its task <c>subscriptions</c> (plan 32 DBR18, plan 22a GP17): the runner takes the subscriptions that are due and, for each, has the exporter draw the document <b>as the subscriber</b> (the subscription service asks
/// <see cref="ISubscriptionRenderer"/>, which is the views module's export under the subscriber's own principal: the subscriber's data permissions, never the runner's) and hands the PDF, CSV or xlsx to the delivery service, which sends it by the subscription's channel.
/// <list type="bullet">
/// <item>It runs <b>on a schedule</b> (<see cref="ViewsExportSettings.Every"/>, a minute by default) and <b>on request</b> (the input <c>subscription</c> names one: a person pressed "send now"; the schedule is not moved).</item>
/// <item>The subscription's own schedule (a cron in its time zone) decides what is due; two runners never take one subscription for one occurrence (<see cref="SubscriptionStore.ClaimDue"/>).</item>
/// <item>The run ends <c>succeeded</c> when every subscription taken sent (or stopped because its subscriber lost access, which is a result, not a failure), <c>completed with failures</c> when some could not render or had no channel (the owners were told), and <b>failed</b> when the host cannot export at all (no browser: the failure names the setting).</item>
/// </list>
/// <b>Where it runs.</b> It is a task of the process that has the views module and the install's state (as <c>quality-system</c> runs in the host's process), because rendering as the subscriber needs the serving core: the Gateway (or the single-server install's console) hosts it. A runner on another machine would call the
/// views module over HTTP with a token on behalf of the subscriber, which is not built (plan 22a, open decision).
/// </summary>
public sealed class ViewsExportTasks(SubscriptionService subscriptions, StateDatabase state, string service = "runner")
{
    public const string Package = "views-export";
    public const string Task = "subscriptions";
    public const string SubscriptionInput = "subscription";

    /// <summary>The manifest of the package, as the <c>package/1</c> YAML the runner's publishing takes.</summary>
    public static string Manifest(ViewsExportSettings? settings = null)
    {
        settings ??= new ViewsExportSettings();
        var task = new PackageTask(Task, new TaskEntry(), Schedule.Interval(settings.Every ?? TimeSpan.FromMinutes(1)), [],
            [new TaskInput(SubscriptionInput, ProgressType.Text, Required: false, Default: null, Description: "A subscription's id: send that one now, whatever its schedule says. Empty: every subscription that is due.")],
            [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var manifest = new PackageManifest(Package, "Renders the subscriptions that are due as their subscribers and delivers them.", new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"),
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
            IReadOnlyList<SubscriptionRunResult> results;
            if (request.Inputs.TryGetValue(SubscriptionInput, out var named) && !string.IsNullOrWhiteSpace(named))
            {
                if (!Guid.TryParse(named, out var id)) throw new ArgumentException($"The input `subscription` is a subscription's id, not '{named}'.");
                results = [await subscriptions.RunNowAsync(id, ct, runId.ToString("D")).ConfigureAwait(false)];
            }
            else
            {
                results = await subscriptions.RunDueAsync(ct, runId.ToString("D")).ConfigureAwait(false);
            }

            var unavailable = results.FirstOrDefault(r => r.Code == "unavailable");
            var failed = results.Where(r => r.Outcome == SubscriptionOutcomes.Failed).ToList();
            if (unavailable is not null)
                end = new TaskEnd(TaskEndState.Failed, Processed: results.Count, Failure: new TaskFailure(TaskFailureKind.Permanent, $"This host cannot export, so nothing was rendered for '{unavailable.Name}': {unavailable.Detail}"));
            else if (failed.Count > 0)
                end = new TaskEnd(TaskEndState.CompletedWithFailures, Processed: results.Count, FailedItems: failed.Count, FirstFailedItem: $"{failed[0].Name}: {failed[0].Detail}");
            else
                end = new TaskEnd(TaskEndState.Succeeded, Processed: results.Count);
        }
        catch (OperationCanceledException)
        {
            end = new TaskEnd(TaskEndState.Stopped);
        }
        catch (KeyNotFoundException ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, ex.Message));
        }
        catch (ArgumentException ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, ex.Message));
        }
        catch (Exception ex)
        {
            end = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Temporary, $"{ex.GetType().Name}: {ex.Message}"));
        }

        var finish = end.ToRunFinish();
        runs.Finish(runId, finish.Outcome, finish.Rows, finish.FailedItem, finish.Failure);
        return new TaskRunResult(runId, end, true, null, false, clock.Elapsed, 0, 0, 0, 0, DbDataKit.Runner.Limits.LimitEnforcement.None, DbDataKit.Runner.Limits.LimitEnforcement.None, "none", 0, false, []);
    }
}

/// <summary>Hands the task of the package <c>views-export</c> to <see cref="ViewsExportTasks"/> (in process) and every other task to the executor (a process per task). A host puts it around its executor like the other system tasks' runners.</summary>
public sealed class ViewsExportTaskRunner(ViewsExportTasks system, ITaskRunner others) : ITaskRunner
{
    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) =>
        request.Task.Package == ViewsExportTasks.Package && request.Task.Task == ViewsExportTasks.Task ? system.RunAsync(request, ct) : others.RunAsync(request, ct);
}
