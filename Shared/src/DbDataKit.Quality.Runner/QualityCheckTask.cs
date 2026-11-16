using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Quality;

/// <summary>What the <c>check</c> task is started with: one dataset (its identity or a name the package's source gives it), or blank for every dataset of the package.</summary>
public sealed record QualityCheckInput(string Dataset = "");

/// <summary>What a package gives the <c>check</c> task: its datasets with checks, the notes about those it could not offer (a connection with no read login, an engine checks do not run on), and the name a person knows a dataset by.</summary>
public sealed record QualityCheckSource(IQualitySource Source, IReadOnlyList<string> Notes, Func<string, string?>? NameOf = null);

/// <summary>
/// The <c>check</c> task (plan 35 I3a): a read-only manual task of a package that evaluates the checks of the package's own datasets again, with the package's own slots and read logins, and records the results through the runner
/// protocol (the kit's <see cref="EngineQuality"/>) under the run's own id, exactly as the task that loads a dataset does after its loads (I3). A check that passes now clears the block it set; nothing is loaded and nothing is written to a
/// target. A package declares it with <see cref="Declare"/> and runs it with a job derived from <see cref="QualityCheckJob"/> that supplies its <see cref="IQualitySource"/>: Build's refresh package does; Sync's and Store's packages declare
/// the same task once they have a source.
/// </summary>
public static class QualityCheckTask
{
    /// <summary>The task's name in every package that declares it.</summary>
    public const string Name = "check";

    /// <summary>The step the run's document is recorded under (the Workspace's Runs tab reads it back).</summary>
    public const string StepName = "check-result";

    /// <summary>The largest document recorded whole (the step's cap is 64 KiB); a larger one keeps its counts and the results that fit, and says so.</summary>
    public const int MaxDocumentBytes = 60 * 1024;

    /// <summary>The longest dataset name the input takes.</summary>
    public const int MaxDatasetLength = 512;

    /// <summary>The task's inputs as the manifest declares them.</summary>
    public static IReadOnlyList<TaskInput> Inputs { get; } =
        [new("dataset", ProgressType.Text, Default: "", Description: "The dataset whose checks are evaluated again (its name or lineage identity); blank: every dataset of the package that has checks")];

    /// <summary>
    /// The task as a package's manifest declares it: by hand only (no schedule of its own; see the plan row), the package's <paramref name="slots"/>, one run at a time, one attempt (a re-check that could not run is started again by a
    /// person), and the step <see cref="StepName"/>.
    /// </summary>
    public static PackageTask Declare(IReadOnlyList<ConnectionSlot> slots, TimeSpan? timeout = null) =>
        new(Name, new TaskEntry(), new Schedule(), [TaskTrigger.ByHand()], Inputs, slots, ProgressDeclaration.Empty,
            new TaskLimits(Timeout: timeout ?? TimeSpan.FromMinutes(30), Concurrency: 1, Attempts: 1), [], [], Steps: [new TaskStep(StepName)]);

    /// <summary>Why a dataset input is refused before anything is opened, or null.</summary>
    public static string? Refusal(string? dataset)
    {
        if (string.IsNullOrEmpty(dataset)) return null;
        if (dataset.Length > MaxDatasetLength) return $"`dataset` is at most {MaxDatasetLength} characters.";
        if (dataset.Trim() != dataset || dataset.Any(char.IsControl)) return "`dataset` is a dataset's name or identity, with no leading or trailing space and no control character.";
        return null;
    }

    /// <summary>The document of a run: the counts, the notes and one line per check (with whether it blocks now), cut to <see cref="MaxDocumentBytes"/>.</summary>
    public static JsonObject Document(QualityRunReport report, string? dataset, IReadOnlyList<string> notes, Func<string, string?>? nameOf = null)
    {
        var results = new JsonArray();
        foreach (var d in report.Datasets)
            foreach (var c in d.Checks)
                results.Add(new JsonObject
                {
                    ["dataset"] = d.Dataset,
                    ["name"] = nameOf?.Invoke(d.Dataset) ?? d.Dataset,
                    ["check"] = c.Check.Id,
                    ["kind"] = CheckNames.Name(c.Check.Kind),
                    ["severity"] = CheckNames.Name(c.Check.Severity),
                    ["status"] = c.Evaluation.Status.ToString().ToLowerInvariant(),
                    ["measured"] = c.Evaluation.Measured,
                    ["threshold"] = c.Evaluation.Threshold,
                    ["sample_keys"] = new JsonArray([.. c.Evaluation.SampleKeys.Select(k => (JsonNode?)JsonValue.Create(k))]),
                    ["message"] = c.Evaluation.Message,
                    ["blocking"] = c.Blocking,
                });

        var document = new JsonObject
        {
            ["task"] = Name,
            ["dataset"] = string.IsNullOrEmpty(dataset) ? null : dataset,
            ["datasets"] = report.Datasets.Count,
            ["evaluated"] = report.Evaluated,
            ["passed"] = report.Evaluated - report.Failed - report.Errors,
            ["failed"] = report.Failed,
            ["errors"] = report.Errors,
            ["blocking"] = report.Blocking,
            ["notes"] = new JsonArray([.. notes.Select(n => (JsonNode?)JsonValue.Create(n))]),
            ["results"] = results,
        };

        if (Size(document) <= MaxDocumentBytes) return document;
        // too large: the sample keys go first, then the results that pass, then whatever does not fit; the counts stay whole
        foreach (var r in results.OfType<JsonObject>()) r["sample_keys"] = new JsonArray();
        for (var i = results.Count - 1; i >= 0 && Size(document) > MaxDocumentBytes; i--)
            if ((string?)results[i]!["status"] == "pass") results.RemoveAt(i);
        while (results.Count > 0 && Size(document) > MaxDocumentBytes) results.RemoveAt(results.Count - 1);
        document["truncated"] = true;
        return document;
    }

    private static int Size(JsonNode node) => System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString());

    /// <summary>The step the document is recorded under, keyed by the task.</summary>
    public static readonly ValueStep<JsonObject, JsonObject> Result = Step.Value<JsonObject, JsonObject>(StepName, _ => Name);
}

/// <summary>
/// The run of the <c>check</c> task (plan 35 I3a): the package's source (<see cref="SourceAsync"/>), the shared <see cref="QualityRunService"/> over the runner's <c>quality</c> module through the protocol (<see cref="EngineQuality"/>; the
/// runner stores each result under this run's id and applies the store's block rules), the document recorded as the step <see cref="QualityCheckTask.StepName"/>. A failing check is what the task is asked to find, so the run succeeds and
/// the document and the Quality pages say it; a dataset the package does not have fails the run. A derived job carries <c>[RunnerTask(QualityCheckTask.Name)]</c>.
/// </summary>
public abstract class QualityCheckJob : RunnerJob<QualityCheckInput, NoProgress, NoSlots>
{
    /// <summary>The package's datasets with checks, opened with the read logins its slots bound. Called once per run.</summary>
    protected abstract Task<QualityCheckSource> SourceAsync(CancellationToken ct);

    protected override async Task RunAsync(CancellationToken ct)
    {
        var dataset = string.IsNullOrEmpty(Input.Dataset) ? null : Input.Dataset;
        if (QualityCheckTask.Refusal(dataset) is { } refusal) throw new TaskFailureException(TaskFailureKind.Permanent, $"{QualityCheckTask.Name}: {refusal}");

        var source = await SourceAsync(ct).ConfigureAwait(false);
        var datasets = await source.Source.DatasetsAsync(ct).ConfigureAwait(false);
        var chosen = dataset is null ? datasets : [.. datasets.Where(d => d.Is(dataset))];
        if (dataset is not null && chosen.Count == 0)
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The dataset '{dataset}' is not one this package has checks for{(datasets.Count == 0 ? " (it has none it can evaluate)" : $"; it has {string.Join(", ", datasets.Take(10).Select(d => source.NameOf?.Invoke(d.Identity) ?? d.Identity))}{(datasets.Count > 10 ? ", ..." : "")}")}.{string.Concat(source.Notes.Select(n => " " + n))}");

        if (Run.DryRun)
        {
            Run.WouldDo($"evaluate {chosen.Sum(d => d.Checks.Count)} check(s) of {chosen.Count} dataset(s) read only and record the results; nothing is loaded or written to a target");
            return;
        }

        Status(dataset is null ? $"check {chosen.Count} dataset(s)" : $"check {dataset}");
        var service = new QualityRunService(new StaticQualitySource(chosen), Quality);
        var report = await service.RunAsync(null, Run.Id.ToString("D"), ct).ConfigureAwait(false);
        var document = QualityCheckTask.Document(report, dataset, source.Notes, source.NameOf);
        await Step(QualityCheckTask.Result, document).Repeatable().RunAsync(_ => Task.FromResult(document), ct).ConfigureAwait(false);
        foreach (var note in source.Notes) Console.Out.WriteLine(note);
        Console.Out.WriteLine($"{report.Evaluated} check(s): {report.Evaluated - report.Failed - report.Errors} passed, {report.Failed} failed, {report.Errors} could not run, {report.Blocking} blocking.");
    }
}
