using System.Globalization;
using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataKit.Quality.Cli;

/// <summary>
/// <c>quality run|show</c> over whatever datasets the host has checks for (plan 23a FQ08). The host decides where the datasets come from (<see cref="IQualitySource"/>) and where the results are kept (the <c>quality</c> module, as a <see cref="QualityStore"/>);
/// the verbs use <see cref="QualityRunService"/>, the service the runner task uses, so a command and a scheduled run cannot differ.
/// </summary>
public static class QualityNoun
{
    private static readonly string CheckSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("kind", DataSchemas.String), ("severity", DataSchemas.String), ("status", DataSchemas.String), ("measured", """{ "type": ["number", "null"] }"""), ("threshold", """{ "type": ["number", "null"] }"""),
            ("observed", """{ "type": ["number", "null"] }"""), ("sample_keys", DataSchemas.StringList), ("message", """{ "type": ["string", "null"] }"""), ("blocking", DataSchemas.Boolean)],
        "id", "kind", "severity", "status", "sample_keys", "blocking");

    private static readonly string DatasetSchema = DataSchemas.Object([("dataset", DataSchemas.String), ("checks", DataSchemas.ListOf(CheckSchema))], "dataset", "checks");

    private static readonly string RunSchema = DataSchemas.Object(
        [("datasets", DataSchemas.ListOf(DatasetSchema)), ("evaluated", DataSchemas.Integer), ("passed", DataSchemas.Integer), ("failed", DataSchemas.Integer), ("errors", DataSchemas.Integer), ("blocking", DataSchemas.Integer)],
        "datasets", "evaluated", "passed", "failed", "errors", "blocking");

    private static readonly string StatusSchema = DataSchemas.Object(
        [("dataset", DataSchemas.String), ("check", DataSchemas.String), ("status", DataSchemas.String), ("severity", DataSchemas.String), ("on_error", DataSchemas.String), ("since", DataSchemas.String), ("evaluated_at", DataSchemas.String),
            ("measured", """{ "type": ["number", "null"] }"""), ("threshold", """{ "type": ["number", "null"] }"""), ("blocking", DataSchemas.Boolean), ("blocked_since", """{ "type": ["string", "null"] }"""),
            ("history", DataSchemas.ListOf(DataSchemas.Object([("status", DataSchemas.String), ("evaluated_at", DataSchemas.String), ("measured", """{ "type": ["number", "null"] }"""), ("sample_keys", DataSchemas.StringList), ("message", """{ "type": ["string", "null"] }"""), ("run", """{ "type": ["string", "null"] }""")], "status", "evaluated_at", "sample_keys")))],
        "dataset", "check", "status", "severity", "on_error", "since", "evaluated_at", "blocking");

    private static readonly string ShowSchema = DataSchemas.Object([("checks", DataSchemas.ListOf(StatusSchema)), ("blocking", DataSchemas.Integer)], "checks", "blocking");

    /// <param name="source">The host's datasets with checks.</param>
    /// <param name="store">The host's results; opened when a verb needs it.</param>
    /// <param name="limits">The limits of a statement; null: the defaults.</param>
    public static Noun Create(IQualitySource source, Func<QualityStore> store, EvaluationLimits? limits = null) => new("quality", "Data quality checks: evaluate a dataset's checks and show their latest results and blocks",
    [
        new Verb("run", "Evaluate the checks of a dataset (or of every dataset) now and record the results", EffectClass.TargetReadOnly, "[--dataset <identity or name>]", (args, context) => RunAsync(source, store, limits, args, context), RunSchema),
        new Verb("show", "The latest result of each check, and what blocks", EffectClass.OfflineOnly, "[--dataset <identity or name>] [--history <n>] [--blocking]", (args, context) => ShowAsync(source, store, args, context), ShowSchema),
    ]);

    private static async Task<int> UsageAsync(CliContext context, string usage)
    {
        await context.Error.WriteLineAsync($"Usage: quality {usage}");
        return CliExit.Usage;
    }

    private static string Number(decimal? value) => value is null ? "-" : value.Value.ToString("0.####", CultureInfo.InvariantCulture);

    private static async Task<int> RunAsync(IQualitySource source, Func<QualityStore> store, EvaluationLimits? limits, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "dataset" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 0) return await UsageAsync(context, "run [--dataset <identity or name>]");
        QualityRunReport report;
        try { report = await new QualityRunService(source, store(), limits).RunAsync(parsed.Get("dataset"), runId: null, context.Cancellation); }
        catch (UnknownDatasetException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }

        context.Data["datasets"] = report.Datasets.Select(d => new Dictionary<string, object?> { ["dataset"] = d.Dataset, ["checks"] = d.Checks.Select(Check).ToList() }).ToList();
        context.Data["evaluated"] = report.Evaluated;
        context.Data["passed"] = report.Evaluated - report.Failed - report.Errors;
        context.Data["failed"] = report.Failed;
        context.Data["errors"] = report.Errors;
        context.Data["blocking"] = report.Blocking;

        if (report.Datasets.Count == 0) await context.Out.WriteLineAsync("No dataset has checks here.");
        foreach (var dataset in report.Datasets)
        {
            await context.Out.WriteLineAsync(dataset.Dataset);
            foreach (var check in dataset.Checks)
            {
                var e = check.Evaluation;
                var line = $"  {e.Status.ToString().ToUpperInvariant(),-5} {check.Check.Id} ({CheckNames.Name(check.Check.Kind)}, {CheckNames.Name(check.Check.Severity)})";
                if (e.Measured is not null || e.Threshold is not null) line += $" measured {Number(e.Measured)}, allowed {Number(e.Threshold)}";
                if (check.Blocking) line += "  BLOCKS dependents";
                await context.Out.WriteLineAsync(line);
                if (e.Message is not null) await context.Out.WriteLineAsync($"      {e.Message}");
                if (e.SampleKeys.Count > 0) await context.Out.WriteLineAsync($"      keys: {string.Join(" | ", e.SampleKeys)}");
            }
        }

        if (report.Evaluated > 0)
            await context.Out.WriteLineAsync($"{report.Evaluated} checks: {report.Evaluated - report.Failed - report.Errors} passed, {report.Failed} failed, {report.Errors} could not run; {report.Blocking} blocking.");
        return report.AllPassed ? CliExit.Ok : CliExit.Failure;
    }

    private static Dictionary<string, object?> Check(CheckRunReport c) => new()
    {
        ["id"] = c.Check.Id,
        ["kind"] = CheckNames.Name(c.Check.Kind),
        ["severity"] = CheckNames.Name(c.Check.Severity),
        ["status"] = c.Evaluation.Status.ToString().ToLowerInvariant(),
        ["measured"] = c.Evaluation.Measured,
        ["threshold"] = c.Evaluation.Threshold,
        ["observed"] = c.Evaluation.Observed,
        ["sample_keys"] = c.Evaluation.SampleKeys,
        ["message"] = c.Evaluation.Message,
        ["blocking"] = c.Blocking,
    };

    private static async Task<int> ShowAsync(IQualitySource source, Func<QualityStore> store, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "blocking" }, new HashSet<string> { "dataset", "history" });
        var history = 0;
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 0 || parsed.Get("history") is { } h && (!int.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture, out history) || history < 1 || history > 200))
            return await UsageAsync(context, "show [--dataset <identity or name>] [--history <1 to 200>] [--blocking]");
        var results = store();
        var known = await source.DatasetsAsync(context.Cancellation);

        IReadOnlyList<CheckStatusRow> rows;
        if (parsed.Get("dataset") is { } name)
        {
            // a dataset the host knows by name or identity; an identity the host does not know (its checks were removed) can still have a status
            var identity = known.FirstOrDefault(d => d.Is(name))?.Identity ?? (name.Contains("://", StringComparison.Ordinal) ? name : null);
            if (identity is null)
            {
                await context.Error.WriteLineAsync($"The dataset '{name}' is not one this host has checks for.");
                return CliExit.Failure;
            }

            rows = results.StatusesOf(identity);
        }
        else rows = [.. known.SelectMany(d => results.StatusesOf(d.Identity)).Concat(results.Blocks()).DistinctBy(r => (r.Dataset, r.CheckId)).OrderBy(r => r.Dataset, StringComparer.Ordinal).ThenBy(r => r.CheckId, StringComparer.Ordinal)];
        if (parsed.Has("blocking")) rows = [.. rows.Where(r => r.Blocking)];

        context.Data["checks"] = rows.Select(r => Status(r, history > 0 ? results.History(r.Dataset, r.CheckId, history) : null)).ToList();
        context.Data["blocking"] = rows.Count(r => r.Blocking);
        if (rows.Count == 0) await context.Out.WriteLineAsync(parsed.Has("blocking") ? "Nothing blocks." : "No check has a result yet.");
        foreach (var row in rows)
        {
            await context.Out.WriteLineAsync($"{row.Dataset}  {row.CheckId}: {row.Outcome.ToString().ToLowerInvariant()} since {row.StatusSince:u} (measured {Number(row.Measured)}, allowed {Number(row.Threshold)}, {row.Severity}){(row.Blocking ? $"  BLOCKS dependents since {row.BlockedSince:u}" : "")}");
            if (history > 0)
                foreach (var old in results.History(row.Dataset, row.CheckId, history))
                    await context.Out.WriteLineAsync($"    {old.EvaluatedAt:u} {old.Outcome.ToString().ToLowerInvariant()} measured {Number(old.Measured)}{(old.SampleKeys.Count > 0 ? $" keys: {string.Join(" | ", old.SampleKeys)}" : "")}{(old.Message is null ? "" : $" ({old.Message})")}");
        }

        return CliExit.Ok;
    }

    private static Dictionary<string, object?> Status(CheckStatusRow r, IReadOnlyList<StoredCheckResult>? history)
    {
        var d = new Dictionary<string, object?>
        {
            ["dataset"] = r.Dataset, ["check"] = r.CheckId, ["status"] = r.Outcome.ToString().ToLowerInvariant(), ["severity"] = r.Severity, ["on_error"] = r.OnError,
            ["since"] = r.StatusSince.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["evaluated_at"] = r.EvaluatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["measured"] = r.Measured, ["threshold"] = r.Threshold, ["blocking"] = r.Blocking, ["blocked_since"] = r.BlockedSince?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        if (history is not null)
            d["history"] = history.Select(h => new Dictionary<string, object?>
            {
                ["status"] = h.Outcome.ToString().ToLowerInvariant(), ["evaluated_at"] = h.EvaluatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["measured"] = h.Measured, ["sample_keys"] = h.SampleKeys, ["message"] = h.Message, ["run"] = h.RunId,
            }).ToList();
        return d;
    }
}
