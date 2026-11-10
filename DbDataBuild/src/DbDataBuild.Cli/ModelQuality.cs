using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataKit.Dialect;
using DbDataKit.Lineage;
using DbDataKit.Quality;
using DbDataKit.State;

namespace DbDataBuild.Cli;

/// <summary>What a refresh's checks did, for the command's words and its document (<c>data.checks</c>).</summary>
internal sealed record ModelQualityOutcome(string RunId, string ResultsIn, QualityRunReport? Report, IReadOnlyList<string> Notes, string? Error, IReadOnlyDictionary<string, string>? ModelOfIdentity = null)
{
    public bool Clean => Error is null && (Report?.AllPassed ?? true);
}

/// <summary>
/// Build's part of the quality checks (plan 23a FQ09): after a refresh, the checks stored with the models that loaded in it are evaluated through the shared evaluator and their results recorded in the <c>quality</c> state module, with the
/// refresh's tracking id as the run id. Build gives <see cref="IQualitySource"/> the datasets: a model is a dataset under the lineage identity its table has on the connection, its checks are the model's (FQ02), its key columns are its
/// <c>unique_key</c> (else its <c>grain</c>), its last load is the refresh that just ended, and the connection is the read login's, opened through <see cref="QualityAccess"/> (read-only). <c>project tests run</c> is another thing and is not touched (QF7).
/// </summary>
internal static class ModelQuality
{
    /// <summary>The environment variable that names the results directory when <c>--check-results</c> is not given (a runner task has no flags of its own).</summary>
    public const string ResultsVariable = "DBDATAKIT_BUILD_CHECK_RESULTS";

    /// <summary>The datasets of the models in <paramref name="models"/> that carry checks and are built on <paramref name="connection"/>.</summary>
    public static (IQualitySource Source, IReadOnlyDictionary<string, string> ModelOfIdentity) Source(ProjectContext ctx, string connection, LoginSettings read, IReadOnlySet<string> models, DateTimeOffset loadedAt, Func<string, string?> env, List<string> notes)
    {
        var datasets = new List<QualityDataset>();
        if (!QualityAccess.Supports(read.Engine))
        {
            var named = ctx.Project.BuiltModels.Where(m => models.Contains(m.Definition.Name) && m.Definition.Checks.Count > 0).Select(m => m.Definition.Name).ToList();
            if (named.Count > 0) notes.Add($"The checks of {string.Join(", ", named)} were not evaluated: the connection `{connection}` is {read.Engine}, and checks run on SQL Server and PostgreSQL.");
            return (new StaticQualitySource(datasets), new Dictionary<string, string>());
        }

        var address = BuildLineage.IdentityOf(ctx.Config, env)(connection);
        var dialect = DialectCatalog.Builtin.Get(read.Engine);
        var built = ctx.Project.BuiltModels.Where(m => ctx.TargetsOf(m.Definition).Contains(connection, StringComparer.Ordinal)).Select(m => m.Definition).ToList();

        // every model of the connection is a name a check may mention (a referential check names another model), by its name and by its identity
        var parts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var identityOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in built)
        {
            var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(model.Name);
            var table = schema is null ? [name] : new[] { schema, name };
            var identity = LineageIdentity.Dataset(address, schema, name).ToString();
            parts[model.Name] = table;
            parts[identity] = table;
            identityOf[model.Name] = identity;
        }

        IReadOnlyList<string>? Resolve(string dataset) => parts.GetValueOrDefault(dataset) is { } known ? known : dataset.Contains("://", StringComparison.Ordinal) ? null : dialect.SplitQualifiedName(dataset);
        var open = QualityAccess.ReadOnlyOpener(read);
        foreach (var model in built.Where(m => models.Contains(m.Name) && m.Checks.Count > 0).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            // the checks are about the model by its name (FQ02); the identity is what results and blocks are stored under
            var identity = identityOf[model.Name];
            var checks = model.Checks.Select(c => c.Check with { Attachment = c.Check.Attachment with { Dataset = identity } }).ToList();
            var keys = model.UniqueKey.Count > 0 ? model.UniqueKey : model.Grain;
            datasets.Add(new QualityDataset(identity, checks, dialect, open, Resolve, Aliases: [model.Name], KeyColumns: keys, LastLoadedAt: loadedAt));
        }

        return (new StaticQualitySource(datasets), identityOf.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// Evaluates and records. Never throws for a failure of the checks themselves (a check that cannot run is an <c>error</c> result); a state database that cannot be opened or written ends in <see cref="ModelQualityOutcome.Error"/>.
    /// </summary>
    public static ModelQualityOutcome Run(string root, string connection, LoginSettings read, IReadOnlyCollection<string> loadedModels, string runId, string resultsDirectory, Func<string, string?> env, DateTimeOffset? at = null)
    {
        var notes = new List<string>();
        try
        {
            var ctx = ProjectContext.Load(root);
            var directory = Path.GetFullPath(resultsDirectory);
            var (source, modelOfIdentity) = Source(ctx, connection, read, loadedModels.ToHashSet(StringComparer.Ordinal), at ?? DateTimeOffset.UtcNow, env, notes);
            var state = DbDataBuild.Cli.Server.BuildState.Open(directory, environment: env);
            var service = new QualityRunService(source, new QualityStore(state));
            var report = Task.Run(() => service.RunAsync(null, runId)).GetAwaiter().GetResult();
            return new ModelQualityOutcome(runId, directory, report, notes, null, modelOfIdentity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the words of the failure only: a state engine's message can carry a connection string
            return new ModelQualityOutcome(runId, resultsDirectory, null, notes, $"The checks were not run: {Failures.Describe(ex)}.");
        }
    }

    /// <summary>The lines under the refresh's summary.</summary>
    public static IEnumerable<string> Lines(ModelQualityOutcome o)
    {
        if (o.Error is not null) { yield return "  " + o.Error; yield break; }
        var r = o.Report!;
        yield return r.Evaluated == 0 ? "  Checks: none to evaluate." : $"  Checks: {r.Evaluated - r.Failed - r.Errors} passed, {r.Failed} failed, {r.Errors} could not run{(r.Blocking > 0 ? $", {r.Blocking} blocking" : "")} (recorded under run {o.RunId}).";
        foreach (var c in r.Checks.Where(c => c.Evaluation.Status != EvaluationStatus.Pass))
        {
            var e = c.Evaluation;
            var what = e.Status == EvaluationStatus.Error ? $"could not run: {e.Message}" : $"failed, measured {Number(e.Measured)} (allowed {Number(e.Threshold)}){(e.SampleKeys.Count > 0 ? $"; for example {string.Join(" | ", e.SampleKeys.Take(3))}" : "")}";
            yield return $"    {CheckNames.Name(c.Check.Severity)} {c.Check.Id} ({CheckNames.Name(c.Check.Kind)}): {what}";
        }
        foreach (var n in o.Notes) yield return "  " + n;
    }

    private static string Number(decimal? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";

    /// <summary>The document under <c>data.checks</c>.</summary>
    public static object Document(ModelQualityOutcome o) => new
    {
        run_id = o.RunId,
        results_in = o.ResultsIn.Replace('\\', '/'),
        evaluated = o.Report?.Evaluated ?? 0,
        passed = o.Report is { } r ? r.Evaluated - r.Failed - r.Errors : 0,
        failed = o.Report?.Failed ?? 0,
        errors = o.Report?.Errors ?? 0,
        blocking = o.Report?.Blocking ?? 0,
        notes = o.Notes,
        error = o.Error,
        results = (o.Report?.Datasets ?? []).SelectMany(d => d.Checks.Select(c => new
        {
            model = o.ModelOfIdentity?.GetValueOrDefault(d.Dataset) ?? d.Dataset,
            check = c.Check.Id,
            kind = CheckNames.Name(c.Check.Kind),
            severity = CheckNames.Name(c.Check.Severity),
            status = c.Evaluation.Status.ToString().ToLowerInvariant(),
            measured = c.Evaluation.Measured,
            threshold = c.Evaluation.Threshold,
            sample_keys = c.Evaluation.SampleKeys,
            message = c.Evaluation.Message,
            blocking = c.Blocking,
        })).ToList(),
    };
}
