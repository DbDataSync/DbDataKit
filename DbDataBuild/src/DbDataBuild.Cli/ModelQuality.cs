using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Semantic;
using DbDataBuild.State;
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

    private static readonly AsyncLocal<IQualityResults?> RunnerResults = new();

    /// <summary>
    /// Where a refresh run as a Runner task records its checks (plan 35 I3): the Runner's <c>quality</c> module through the protocol (the task's <c>EngineQuality</c>), set by the refresh task for the flow of the command it runs. With it set, a refresh
    /// evaluates its checks after the loads with no results directory, and a model a check blocks is held back by what the Runner keeps; <c>--check-results</c> (or <see cref="ResultsVariable"/>) still names a directory of its own instead.
    /// </summary>
    public static IQualityResults? Runner => RunnerResults.Value;

    /// <summary>Makes <paramref name="results"/> where the checks of the current flow are recorded until the returned scope is disposed.</summary>
    public static IDisposable UseRunner(IQualityResults? results)
    {
        var previous = RunnerResults.Value;
        RunnerResults.Value = results;
        return new Restore(previous);
    }

    private sealed class Restore(IQualityResults? previous) : IDisposable
    {
        public void Dispose() => RunnerResults.Value = previous;
    }

    /// <summary>What a document says the results are in when the Runner keeps them.</summary>
    public const string RunnerResultsIn = "runner";

    /// <summary>The datasets of the models in <paramref name="models"/> that carry checks and are built on <paramref name="connection"/>.</summary>
    public static (IQualitySource Source, IReadOnlyDictionary<string, string> ModelOfIdentity) Source(ProjectContext ctx, string connection, LoginSettings read, IReadOnlySet<string> models, DateTimeOffset loadedAt, Func<string, string?> env, List<string> notes) =>
        Source(ctx, connection, read, models, _ => loadedAt, env, notes);

    /// <summary>The same, with when each model was last loaded (<paramref name="loadedAt"/> by the model's name; null when no successful load is known).</summary>
    public static (IQualitySource Source, IReadOnlyDictionary<string, string> ModelOfIdentity) Source(ProjectContext ctx, string connection, LoginSettings read, IReadOnlySet<string> models, Func<string, DateTimeOffset?> loadedAt, Func<string, string?> env, List<string> notes)
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
        foreach (var model in built.Where(m => models.Contains(m.Name) && (m.Checks.Count > 0 || KeyChecksOf(m, "").Count > 0)).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            // the checks are about the model by its name (FQ02); the identity is what results and blocks are stored under
            var identity = identityOf[model.Name];
            var checks = model.Checks.Select(c => c.Check with { Attachment = c.Check.Attachment with { Dataset = identity } }).Concat(KeyChecksOf(model, identity)).ToList();
            var keys = model.UniqueKey.Count > 0 ? model.UniqueKey : model.Grain;
            datasets.Add(new QualityDataset(identity, checks, dialect, open, Resolve, Aliases: [model.Name], KeyColumns: keys, LastLoadedAt: loadedAt(model.Name)));
        }

        return (new StaticQualitySource(datasets), identityOf.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// The datasets of a package's <c>check</c> task (plan 35 I3a): every model of the project with checks, on each connection whose read login the environment has (on a Runner, the slots bound to the package; a connection without one
    /// is a note, never another package's login), with the last successful load of each model read from the connection's tracking tables with the read login (for a <c>freshness</c> check without a column). Nothing is loaded and
    /// nothing is written: the checks run over <see cref="QualityAccess"/>'s read-only connections.
    /// </summary>
    public static async Task<QualityCheckSource> PackageSourceAsync(string root, Func<string, string?> env, CancellationToken ct = default)
    {
        var ctx = ProjectContext.Load(root);
        var notes = new List<string>();
        var datasets = new List<QualityDataset>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var connections = ctx.Project.BuiltModels.SelectMany(m => ctx.TargetsOf(m.Definition)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var connection in connections)
        {
            var models = ctx.Project.BuiltModels.Select(m => m.Definition)
                .Where(m => ctx.TargetsOf(m).Contains(connection, StringComparer.Ordinal) && (m.Checks.Count > 0 || KeyChecksOf(m, "").Count > 0)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            if (models.Count == 0) continue;
            var (read, missing) = LoginSettings.FromEnvironment(ctx.Config, connection, Login.Read, env);
            if (read is null)
            {
                notes.Add($"The checks of {string.Join(", ", models.Order(StringComparer.Ordinal))} were not evaluated: the package has no read login for `{connection}` ({missing?.Found ?? "none is bound"}).");
                continue;
            }

            var loads = await LastLoadsAsync(ctx, connection, read, env, notes, ct).ConfigureAwait(false);
            var (source, modelOf) = Source(ctx, connection, read, models, model => loads.GetValueOrDefault(model), env, notes);
            datasets.AddRange(await source.DatasetsAsync(ct).ConfigureAwait(false));
            foreach (var (identity, model) in modelOf) names[identity] = model;
        }

        if (datasets.Count == 0 && notes.Count == 0) notes.Add("No model of the project has checks.");
        return new QualityCheckSource(new StaticQualitySource(datasets), notes, identity => names.GetValueOrDefault(identity));
    }

    /// <summary>When each model of <paramref name="connection"/> last loaded successfully, from its tracking tables (<c>run_log</c>) with the read login; none when nothing is tracked or the tables cannot be read (a note says so).</summary>
    private static async Task<IReadOnlyDictionary<string, DateTimeOffset?>> LastLoadsAsync(ProjectContext ctx, string connection, LoginSettings read, Func<string, string?> env, List<string> notes, CancellationToken ct)
    {
        var found = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        if (ctx.Config.TrackingOf(connection).Target is not { } target) return found;
        try
        {
            var login = read;
            if (target.Connection != connection)
            {
                var (other, missing) = LoginSettings.FromEnvironment(target.Connection, target.Engine, Login.Read, env);
                if (other is null) { notes.Add($"When the models of `{connection}` were last loaded was not read: {missing?.Found}"); return found; }
                login = other;
            }

            await using var session = await ReadSession.OpenAsync(login, ct).ConfigureAwait(false);
            var loads = await BuildLineage.LastLoadsAsync(session, new TrackingScope(target.Engine, target.SchemaName, connection), ct).ConfigureAwait(false);
            foreach (var l in loads.Where(l => l.Succeeded)) found[l.Model] = new DateTimeOffset(DateTime.SpecifyKind(l.EndedUtc, DateTimeKind.Utc));
            return found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            notes.Add($"When the models of `{connection}` were last loaded was not read ({Failures.Describe(ex)}); a freshness check without a column cannot measure its age.");
            return found;
        }
    }

    /// <summary>
    /// The collision guard of a model's hashed keys as framework checks (plan 25 K06b): a <c>unique</c> check with severity <c>block</c> on each key column (the version key of a key with history), so a repeated key value fails the refresh's checks
    /// and blocks the model's dependents. Nobody writes these: they follow from <c>keys:</c>. A check the model's file already declares under the same id wins.
    /// </summary>
    internal static IReadOnlyList<CheckDefinition> KeyChecksOf(ModelDefinition model, string dataset) =>
        model.Keys.Where(k => k.Method == KeyMethods.Hash).Select(k => KeyChecks.UniqueColumn(k, model.Columns)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(column => model.Checks.All(c => c.Check.Id != KeyChecks.FrameworkCheckId(column)))
            .Select(column => new CheckDefinition(KeyChecks.FrameworkCheckId(column), CheckKind.Unique, new CheckAttachment(dataset, [column]), NoParameters.Instance, CheckSeverity.Block, CheckOnError.Alert,
                $"every value of `{column}` is on one row of `{model.Name}` (a repeat is a business key that appears twice, two business keys whose hashes collided, or a part that holds the separator U+001F)"))
            .ToList();

    /// <summary>
    /// Evaluates and records. Never throws for a failure of the checks themselves (a check that cannot run is an <c>error</c> result); a state database that cannot be opened or written ends in <see cref="ModelQualityOutcome.Error"/>.
    /// </summary>
    public static ModelQualityOutcome Run(string root, string connection, LoginSettings read, IReadOnlyCollection<string> loadedModels, string runId, string resultsDirectory, Func<string, string?> env, DateTimeOffset? at = null) =>
        Run(root, connection, read, loadedModels, runId, resultsDirectory, () => new QualityStore(BuildState.Open(Path.GetFullPath(resultsDirectory), environment: env)), env, at);

    /// <summary>The same, recording in <paramref name="results"/> (the Runner's, for a refresh run as a Runner task); <paramref name="resultsIn"/> is what the document says they are in.</summary>
    public static ModelQualityOutcome Run(string root, string connection, LoginSettings read, IReadOnlyCollection<string> loadedModels, string runId, string resultsIn, Func<IQualityResults> results, Func<string, string?> env, DateTimeOffset? at = null)
    {
        var notes = new List<string>();
        try
        {
            var ctx = ProjectContext.Load(root);
            var directory = resultsIn == RunnerResultsIn ? resultsIn : Path.GetFullPath(resultsIn);
            var (source, modelOfIdentity) = Source(ctx, connection, read, loadedModels.ToHashSet(StringComparer.Ordinal), at ?? DateTimeOffset.UtcNow, env, notes);
            var service = new QualityRunService(source, results());
            var report = Task.Run(() => service.RunAsync(null, runId)).GetAwaiter().GetResult();
            return new ModelQualityOutcome(runId, directory, report, notes, null, modelOfIdentity);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the words of the failure only: a state engine's message can carry a connection string
            return new ModelQualityOutcome(runId, resultsIn, null, notes, $"The checks were not run: {Failures.Describe(ex)}.");
        }
    }

    /// <summary>
    /// The datasets of this connection's models that a check blocks now (plan 23a FQ14), read from the results directory's <c>quality</c> state: the model's name and the reason a model that reads it gives. A state that cannot be read blocks nothing
    /// (a check that cannot be read has not shown the data to be bad, QF10) and the reason is returned in <paramref name="problem"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Blocks(string root, string connection, string resultsDirectory, Func<string, string?> env, out string? problem) =>
        Blocks(root, connection, () => new QualityStore(BuildState.Open(Path.GetFullPath(resultsDirectory), environment: env)).Blocks(), env, out problem);

    /// <summary>The same, from <paramref name="results"/> (the Runner's, for a refresh run as a Runner task).</summary>
    public static IReadOnlyDictionary<string, string> Blocks(string root, string connection, IQualityResults results, Func<string, string?> env, out string? problem) =>
        Blocks(root, connection, () => Task.Run(() => results.BlocksAsync()).GetAwaiter().GetResult(), env, out problem);

    private static IReadOnlyDictionary<string, string> Blocks(string root, string connection, Func<IReadOnlyList<CheckStatusRow>> blocks, Func<string, string?> env, out string? problem)
    {
        problem = null;
        try
        {
            var ctx = ProjectContext.Load(root);
            var address = BuildLineage.IdentityOf(ctx.Config, env)(connection);
            var identityOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var model in ctx.Project.BuiltModels.Where(m => ctx.TargetsOf(m.Definition).Contains(connection, StringComparer.Ordinal)).Select(m => m.Definition))
            {
                var (schema, name) = DbDataBuild.Targets.Ddl.DdlGenerator.Split(model.Name);
                identityOf[LineageIdentity.Dataset(address, schema, name).ToString()] = model.Name;
            }

            var blocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in blocks())
                if (identityOf.TryGetValue(block.Dataset, out var model) && !blocked.ContainsKey(model))
                    blocked[model] = $"{model} is blocked by the check {block.CheckId} since {block.BlockedSince:u}";
            return blocked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = $"The checks that block were not read, so nothing was held back: {Failures.Describe(ex)}.";
            return new Dictionary<string, string>();
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
