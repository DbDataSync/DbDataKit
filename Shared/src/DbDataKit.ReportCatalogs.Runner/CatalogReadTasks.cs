using DbDataKit.Views;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.ReportCatalogs;

/// <summary>How often the task reads the catalogs (a report server's items change rarely: fifteen minutes by default).</summary>
public sealed record CatalogReadSettings(TimeSpan? Every = null);

/// <summary>One catalog the task reads: its name (the provider's name), the provider (signed in as the catalog's service account) and where its snapshot goes (the sink of the channel the task runs for).</summary>
public sealed record CatalogSource(string Name, IReportCatalogProvider Provider, IReportCatalogSnapshotSink Sink);

/// <summary>
/// The system package <c>report-catalogs</c> and its task <c>read</c> (plan 32 RC02): for each external catalog the host knows (the connections of kind <c>report-catalog</c> bound in the channel) the task reads the folders, items, parameters and policies as the catalog's service account
/// and publishes a <c>report-catalog/1</c> snapshot through plan 30's registry (<see cref="DefinitionStoreSnapshotSink"/>) when it changed. A failing read leaves the last snapshot where it was.
/// <list type="bullet">
/// <item>It runs <b>on a schedule</b> (<see cref="CatalogReadSettings.Every"/>) and <b>on request</b> (the input <c>catalog</c> names one: a person pressed "read now").</item>
/// <item>The run ends <c>succeeded</c> when every catalog read (published or unchanged), <c>completed with failures</c> when some did not (the failed item names the catalog and the provider's words, which name the address and the service account), and <b>failed</b> when none did.</item>
/// </list>
/// It is a task of the process that holds the install's state (as <c>views-export</c> is), because the snapshot goes into the registry there.
/// </summary>
public sealed class CatalogReadTasks(Func<IReadOnlyList<CatalogSource>> sources, StateDatabase state, string service = "runner")
{
    public const string Package = "report-catalogs";
    public const string Task = "read";
    public const string CatalogInput = "catalog";

    /// <summary>The manifest of the package, as the <c>package/1</c> YAML the runner's publishing takes.</summary>
    public static string Manifest(CatalogReadSettings? settings = null)
    {
        settings ??= new CatalogReadSettings();
        var task = new PackageTask(Task, new TaskEntry(), Schedule.Interval(settings.Every ?? TimeSpan.FromMinutes(15)), [],
            [new TaskInput(CatalogInput, ProgressType.Text, Required: false, Default: null, Description: "A catalog's name: read that one now. Empty: every external catalog.")],
            [], ProgressDeclaration.Empty, TaskLimits.None, [], []);
        var manifest = new PackageManifest(Package, "Reads each external report catalog and publishes a snapshot when it changed.", new PackageLaunch(LaunchKind.Executable, Command: "dbdatakit"),
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
            var all = sources();
            IReadOnlyList<CatalogSource> chosen = all;
            if (request.Inputs.TryGetValue(CatalogInput, out var named) && !string.IsNullOrWhiteSpace(named))
            {
                chosen = [.. all.Where(s => string.Equals(s.Name, named, StringComparison.Ordinal))];
                if (chosen.Count == 0) throw new KeyNotFoundException($"There is no external report catalog '{named}'; the catalogs are {(all.Count == 0 ? "none" : string.Join(", ", all.Select(s => $"'{s.Name}'")))}.");
            }

            var failed = new List<(string Name, string Reason)>();
            foreach (var source in chosen)
            {
                ct.ThrowIfCancellationRequested();
                var result = await new ReportCatalogSnapshotJob(source.Provider, source.Sink).RunAsync(ct).ConfigureAwait(false);
                if (result.Outcome == ReportCatalogReadResult.Failed) failed.Add((source.Name, result.Reason ?? "the read failed"));
            }

            if (failed.Count == 0) end = new TaskEnd(TaskEndState.Succeeded, Processed: chosen.Count);
            else if (failed.Count == chosen.Count)
                end = new TaskEnd(TaskEndState.Failed, Processed: chosen.Count, FailedItems: failed.Count, FirstFailedItem: $"{failed[0].Name}: {failed[0].Reason}", Failure: new TaskFailure(TaskFailureKind.Temporary, $"No catalog could be read; the last snapshots stay. {failed[0].Name}: {failed[0].Reason}"));
            else
                end = new TaskEnd(TaskEndState.CompletedWithFailures, Processed: chosen.Count, FailedItems: failed.Count, FirstFailedItem: $"{failed[0].Name}: {failed[0].Reason}");
        }
        catch (OperationCanceledException)
        {
            end = new TaskEnd(TaskEndState.Stopped);
        }
        catch (KeyNotFoundException ex)
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

/// <summary>Hands the task of the package <c>report-catalogs</c> to <see cref="CatalogReadTasks"/> (in process) and every other task to the executor. A host puts it around its executor like the other system tasks' runners.</summary>
public sealed class CatalogReadTaskRunner(CatalogReadTasks system, ITaskRunner others) : ITaskRunner
{
    public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) =>
        request.Task.Package == CatalogReadTasks.Package && request.Task.Task == CatalogReadTasks.Task ? system.RunAsync(request, ct) : others.RunAsync(request, ct);
}
