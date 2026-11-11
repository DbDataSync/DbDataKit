using DbDataKit.State;

namespace DbDataKit.Quality;

/// <summary>One check's result after it was evaluated and recorded: the evaluation, the check's own settings and whether the check blocks its dataset's dependents now (the store's block rules).</summary>
public sealed record CheckRunReport(CheckDefinition Check, CheckEvaluation Evaluation, bool Blocking);

/// <summary>The checks of one dataset.</summary>
public sealed record DatasetRunReport(string Dataset, IReadOnlyList<CheckRunReport> Checks);

/// <summary>What one run of the service did.</summary>
public sealed record QualityRunReport(IReadOnlyList<DatasetRunReport> Datasets)
{
    public IEnumerable<CheckRunReport> Checks => Datasets.SelectMany(d => d.Checks);

    public int Evaluated => Checks.Count();

    public int Failed => Checks.Count(c => c.Evaluation.Status == EvaluationStatus.Fail);

    public int Errors => Checks.Count(c => c.Evaluation.Status == EvaluationStatus.Error);

    public int Blocking => Checks.Count(c => c.Blocking);

    /// <summary>True when every check passed.</summary>
    public bool AllPassed => Failed == 0 && Errors == 0;
}

/// <summary>The dataset a run was asked for is not one the source knows.</summary>
public sealed class UnknownDatasetException(string name) : Exception($"The dataset '{name}' is not one this host has checks for.")
{
    public string Dataset { get; } = name;
}

/// <summary>
/// Evaluates the checks of a dataset (or of every dataset the source has) and writes the results through <see cref="QualityStore"/> (plan 23a FQ08): one <c>check_result</c> per check, the check's <c>check_status</c> and its block set or cleared in the
/// same transaction (the store's rules: a <c>block</c> check blocks while its latest result is <c>fail</c>; an error blocks only with <c>on_error: block</c> and lifts nothing). The shared command (<c>quality run</c>) and the runner task use this one service, so they
/// cannot differ. A <c>row_count_change</c> check is compared with the previous successful evaluation of the same check (<see cref="QualityStore.Baseline"/>, QF3), and the first evaluation only records.
/// </summary>
public sealed class QualityRunService(IQualitySource source, QualityStore store, EvaluationLimits? limits = null, TimeProvider? time = null)
{
    /// <summary>Evaluates and records. <paramref name="dataset"/> is a dataset's identity or one of its names; null is every dataset of the source. <paramref name="runId"/> is stored with each result (the runner's run, Build's tracking id).</summary>
    /// <exception cref="UnknownDatasetException">A named dataset is not in the source.</exception>
    public async Task<QualityRunReport> RunAsync(string? dataset, string? runId = null, CancellationToken ct = default)
    {
        var all = await source.DatasetsAsync(ct).ConfigureAwait(false);
        var selected = dataset is null ? all : [.. all.Where(d => d.Is(dataset))];
        if (dataset is not null && selected.Count == 0) throw new UnknownDatasetException(dataset);

        var reports = new List<DatasetRunReport>();
        foreach (var one in selected)
        {
            ct.ThrowIfCancellationRequested();
            var evaluator = new CheckEvaluator(one.Dialect, limits, time);
            // the baselines are read before the statements go out: a row-count check compares with the evaluation before this one
            var baselines = one.Checks.Where(c => c.Kind == CheckKind.RowCountChange).ToDictionary(c => c.Id, c => store.Baseline(one.Identity, c.Id), StringComparer.Ordinal);
            var results = await evaluator.EvaluateAsync(one.Checks, one.OpenReadOnly, check => new EvaluationContext
            {
                ResolveTable = one.ResolveTable,
                KeyColumns = one.KeyColumns ?? [],
                LastLoadedAt = one.LastLoadedAt,
                PreviousRowCount = baselines.GetValueOrDefault(check.Id),
            }, ct).ConfigureAwait(false);

            var checks = new List<CheckRunReport>();
            for (var i = 0; i < one.Checks.Count; i++)
            {
                var check = one.Checks[i];
                var result = results[i];
                var status = store.Record(new CheckResultRecord(check.Id, one.Identity, Outcome(result.Status), result.EvaluatedAt, result.Measured, result.Threshold, result.SampleKeys, runId,
                    CheckNames.Name(check.Severity), CheckNames.Name(check.OnError), result.Message, result.Observed));
                checks.Add(new CheckRunReport(check, result, status.Blocking));
            }

            reports.Add(new DatasetRunReport(one.Identity, checks));
        }

        return new QualityRunReport(reports);
    }

    private static CheckOutcome Outcome(EvaluationStatus status) => status switch
    {
        EvaluationStatus.Pass => CheckOutcome.Pass,
        EvaluationStatus.Fail => CheckOutcome.Fail,
        _ => CheckOutcome.Error,
    };
}
