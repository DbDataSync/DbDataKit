using System.Globalization;
using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.Semantic;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild connection check` (effect: target read-only; plan 14 R1, plan 19 J09). Runs the data checks generated from the project's semantic models (<see cref="SemanticChecks"/>) against a connection: each hierarchy
/// level is many-to-one onto the one above, every key of a relationship's many side exists on its one side, and the one side's keys are unique. Each check is one SELECT that returns the violations, on the read login.
/// Like `connection compare`, no value is read back unless `--show-values` asks: by default the output is counts, so a person can run it without real keys on screen.
/// </summary>
internal static class CheckCommand
{
    private sealed record Outcome(SemanticCheck Check, string Status, long Violations, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Examples, string? Error);

    public static int Run(CommandSpec spec, string root, string? targetArg, bool showValues, int limit, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (limit < 0) { error.WriteLine("--limit must not be negative."); return CliApp.ExitUsage; }
        var ctx = ProjectContext.Load(root);
        var connection = CommandTargets.Resolve(ctx.Config, targetArg, error, readOnlyOk: true);
        if (connection == null) return CliApp.ExitUsage;
        if (!connection.Kind.IsBuildTarget)
        {
            error.WriteLine($"`connection check` reads SQL Server and PostgreSQL connections; `{connection.Name}` is {connection.Kind.Describe()}.");
            return CliApp.ExitUsage;
        }

        var (login, missing) = LoginSettings.FromEnvironment(connection, Login.Read, env);
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: {connection.Name}  |  login: {login?.Describe() ?? "none"}  |  values: {(showValues ? "shown (requested)" : "not read")}");

        var problems = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Location.File.StartsWith(SemanticProject.Folder + "/", StringComparison.Ordinal)).ToList();
        foreach (var d in problems) error.Diag(d);
        if (problems.Count > 0)
        {
            output.WriteLine($"Nothing was checked: {problems.Count} error(s) in the semantic files. Fix them (`{ProductInfo.Cli} project compile` shows them).");
            return CliApp.ExitFindings;
        }
        var generated = ctx.Semantic.Models.Select(m => SemanticChecks.Generate(m, ctx.Config)).ToList();
        var foreignKeys = ForeignKeyChecks.Generate(ProjectModels.Of(ctx.Project, ctx.Config.DefaultConnections), ctx.Config);
        var checks = generated.SelectMany(g => g.Checks).Concat(foreignKeys).Where(c => c.Connection == connection.Name).ToList();
        var skipped = generated.SelectMany(g => g.Skipped).ToList();
        if (checks.Count == 0)
        {
            output.Payload("connection", connection.Name);
            output.Payload("counts", new { total = 0, passed = 0, failed = 0, errored = 0 });
            output.Payload("checks", Array.Empty<object>());
            output.Payload("skipped", Skipped(skipped));
            output.WriteLine(ctx.Semantic.Models.Count == 0 ? $"No checks: the project has no semantic models ({SemanticProject.Folder}/) and no foreign keys on its models." : $"No checks: no hierarchy, relationship or foreign key is on `{connection.Name}`.");
            return CliApp.ExitOk;
        }
        if (missing != null) { error.Diag(missing); return CliApp.ExitFindings; }

        var outcomes = new List<Outcome>();
        try
        {
            Task.Run(async () =>
            {
                await using var read = await ReadSession.OpenAsync(login!);
                read.CommandTimeoutSeconds = 0;       // a check scans whole tables
                foreach (var check in checks) outcomes.Add(await RunOne(read, check, showValues, limit));
            }).GetAwaiter().GetResult();
        }
        catch (GateRefusedException ex) { error.Diag(ex.Diagnostic); return CliApp.ExitFindings; }

        foreach (var o in outcomes.Where(o => o.Status != "pass"))
        {
            var at = new SourceLocation(o.Check.File, o.Check.Line, o.Check.Column);
            error.Diag(o.Status == "fail"
                ? new Diagnostic(DiagnosticCatalog.TestFailed, at, $"`{o.Check.Name}`: {o.Check.Description} fails for {o.Violations} {(o.Check.Kind == "relationship_orphans" ? "key(s)" : o.Check.Kind == "hierarchy" ? "member(s)" : "key(s)")}.")
                : new Diagnostic(DiagnosticCatalog.TestCouldNotRun, at, $"`{o.Check.Name}` could not run: {o.Error}."));
        }

        var counts = new { total = outcomes.Count, passed = outcomes.Count(o => o.Status == "pass"), failed = outcomes.Count(o => o.Status == "fail"), errored = outcomes.Count(o => o.Status == "error") };
        output.Payload("connection", connection.Name);
        output.Payload("counts", counts);
        output.Payload("checks", outcomes.Select(o => new
        {
            name = o.Check.Name, kind = o.Check.Kind, catalog = o.Check.Catalog, description = o.Check.Description, models = o.Check.Models, file = o.Check.File, line = o.Check.Line,
            status = o.Status, violations = o.Violations, columns = showValues && o.Examples.Count > 0 ? o.Columns : null,
            examples = showValues && o.Examples.Count > 0 ? o.Examples.Select(r => r.Select(Show).ToList()).ToList() : null, error = o.Error,
        }).ToList());
        output.Payload("skipped", Skipped(skipped));

        output.WriteLine();
        foreach (var o in outcomes)
        {
            output.WriteLine($"{(o.Status switch { "pass" => "pass", "fail" => "FAIL", _ => "ERROR" }),-5} {o.Check.Name}{(o.Status == "fail" ? $"  ({o.Violations} violation(s))" : "")}");
            if (o.Error != null) output.WriteLine($"      {o.Error}");
            if (showValues) foreach (var row in o.Examples) output.WriteLine("      " + string.Join(" | ", o.Columns.Zip(row, (c, v) => $"{c}={Show(v)}")));
        }
        foreach (var s in skipped) output.WriteLine($"not checked: {s.Description}: {s.Reason}.");
        output.WriteLine();
        output.WriteLine($"{counts.total} check(s): {counts.passed} passed, {counts.failed} failed, {counts.errored} could not run.");
        return counts.failed + counts.errored > 0 ? CliApp.ExitFindings : CliApp.ExitOk;
    }

    private static object Skipped(IEnumerable<SemanticCheckSkipped> skipped) => skipped.Select(s => new { catalog = s.Catalog, description = s.Description, reason = s.Reason, file = s.File, line = s.Line }).ToList();

    private static async Task<Outcome> RunOne(ReadSession read, SemanticCheck check, bool showValues, int limit)
    {
        try
        {
            var counted = await read.QueryAsync($"SELECT COUNT(*) FROM ({check.Sql}) v");
            var violations = Convert.ToInt64(counted[0][0], CultureInfo.InvariantCulture);
            if (violations == 0 || !showValues || limit == 0) return new Outcome(check, violations == 0 ? "pass" : "fail", violations, [], [], null);
            var (names, rows) = await read.QueryFirstRowsAsync(check.Sql + " ORDER BY 1", limit, 0);
            return new Outcome(check, "fail", violations, names, rows, null);
        }
        catch (GateRefusedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Outcome(check, "error", 0, [], [], Failures.Describe(ex));
        }
    }

    private static string Show(object? v) => v switch
    {
        null => "NULL",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}
