using System.Text.Json;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;

namespace DbDataBuild.Cli.Runner;

/// <summary>What a refresh task is started with: the connection and the plan (the manifest's inputs `connection` and `plan`, with the plan's default `default`).</summary>
public sealed record RefreshInput(string Connection, string Plan = "default");

public static class RefreshUnits
{
    /// <summary>One unit per load of the plan, keyed `model.load`.</summary>
    public static readonly Unit<LoadResult> Load = Unit.Of<LoadResult>("load", l => l.Name);
}

public static class RefreshCounters
{
    public static readonly Counter Rows = new("rows");
}

/// <summary>One load of a refresh as the command's JSON document reports it (`data.results`).</summary>
public sealed record LoadResult(string Name, string State, string? Detail, long Rows);

/// <summary>
/// One refresh plan as a runner task (plan 28c E01): runs `connection refresh &lt;connection&gt; &lt;plan&gt; --project &lt;package directory&gt;` in this process through <see cref="CliApp"/>, so the rows written to the target
/// and the tracking rows are exactly what the same command gives, and maps the command's document onto the run's end: no failed load is `Succeeded`, some failed loads with others done (`on_failure: continue`) is `CompletedWithFailures`,
/// and a refused or failed plan is `Failed` with the diagnostic code or the load named. The logins come from `DBDATAKIT_BUILD_&lt;CONNECTION&gt;_&lt;READ|WRITE&gt;` in the environment, as for the command.
/// </summary>
[RunnerTask("refresh")]
public sealed class RefreshJob : RunnerJob<RefreshInput, NoProgress, NoSlots>
{
    /// <summary>Where the project (the published `rendered/` and `dbdatabuild.yml`) is: the runner's read-only view of the package.</summary>
    public const string PackageDirectoryVariable = DbDataKit.Runner.Protocol.RunnerProtocolNames.PackageDirectoryVariable;

    protected override async Task RunAsync(CancellationToken ct)
    {
        var project = Environment.GetEnvironmentVariable(PackageDirectoryVariable);
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The package directory ({PackageDirectoryVariable}) is not set or does not exist; a refresh task runs from the project the package holds.");

        var actor = $"runner {Run.Package}/{Run.Channel}";
        // the view of the package is read-only: what the command writes beside the project (its statement log) goes to the run's scratch directory
        var scratch = Environment.GetEnvironmentVariable(DbDataKit.Runner.Protocol.RunnerProtocolNames.ScratchVariable);
        string? Env(string name) =>
            name == Actor.Variable ? actor
            : name == InitCommand.StatementLogVariable && !string.IsNullOrEmpty(scratch) ? Path.Combine(scratch, "statement-log")
            : Environment.GetEnvironmentVariable(name);
        var args = new[] { "connection", "refresh", Input.Connection, Input.Plan, "--project", project, "--format", "json" };
        Status($"refresh {Input.Connection} {Input.Plan}");
        var (exit, document) = await Task.Run(() => Invoke(args, Env), ct).ConfigureAwait(false);
        using var _ = document;

        var data = document.RootElement.TryGetProperty("data", out var d) ? d : default;
        if (exit == CliApp.ExitBusy) throw new TaskFailureException(TaskFailureKind.Temporary, $"Another refresh of `{Input.Connection}` holds the lock ({Describe(document)}).");

        var results = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? Results(r) : null;
        if (results is null) throw FailureOf(exit, document);

        long rows = 0;
        foreach (var l in results) if (l.State == "done") rows += l.Rows;
        Count(RefreshCounters.Rows, rows);
        await ForEachAsync(RefreshUnits.Load, results, parallelism: 1, (load, _) =>
        {
            if (load.State == "failed") throw new TaskFailureException(TaskFailureKind.Permanent, $"The load `{load.Name}` failed{(string.IsNullOrEmpty(load.Detail) ? "" : ": " + load.Detail)}", load.Name);
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        var outcome = data.TryGetProperty("outcome", out var o) ? o.GetString() : null;
        if (outcome is "failed" or "stopped" || (exit != CliApp.ExitOk && !results.Any(l => l.State == "failed")))
        {
            var first = results.FirstOrDefault(l => l.State == "failed");
            throw new TaskFailureException(TaskFailureKind.Permanent, first is null ? $"The refresh ended {outcome ?? "with exit code " + exit}: {Describe(document)}" : $"The load `{first.Name}` failed and no load of the plan finished", first?.Name);
        }
    }

    private static (int Exit, JsonDocument Document) Invoke(string[] args, Func<string, string?> env)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var exit = CliApp.Run(args, output, error, TextReader.Null, interactive: false, environment: env);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output.ToString());
        }
        catch (JsonException)
        {
            // a command that failed before it wrote its document (the guard writes text); keep what it said
            document = JsonDocument.Parse(JsonSerializer.Serialize(new { exit_code = exit, messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), errors = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries) }));
        }

        foreach (var line in Lines(document, "messages")) Console.Out.WriteLine(line);
        foreach (var line in Lines(document, "errors")) Console.Error.WriteLine(line);
        return (exit, document);
    }

    private static IEnumerable<string> Lines(JsonDocument document, string name) =>
        document.RootElement.TryGetProperty(name, out var lines) && lines.ValueKind == JsonValueKind.Array ? lines.EnumerateArray().Select(l => l.GetString() ?? "").ToList() : [];

    private static List<LoadResult> Results(JsonElement results) => results.EnumerateArray().Select(e =>
    {
        string Text(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var model = Text("model");
        var load = Text("member") is { Length: > 0 } member ? member : Text("load");
        return new LoadResult(model + "." + load, Text("state"), Text("detail") is { Length: > 0 } detail ? detail : null, e.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Number ? rows.GetInt64() : 0);
    }).ToList();

    /// <summary>The diagnostics of the document as `code: title (what was found)`, which is what names a refused plan (DDB-450 for a plan that does not exist, DDB-447 for a structure the project does not expect).</summary>
    private static string Describe(JsonDocument document) =>
        document.RootElement.TryGetProperty("diagnostics", out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join("; ", list.EnumerateArray().Select(x => $"{x.GetProperty("code").GetString()}: {x.GetProperty("title").GetString()}{(x.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.String ? " (" + found.GetString() + ")" : "")}"))
            : "";

    /// <summary>A command that ran no loads: refused (usage or findings, with the diagnostic named) or failed (the database or the tool).</summary>
    private TaskFailureException FailureOf(int exit, JsonDocument document)
    {
        var said = Describe(document);
        // a command that printed no diagnostic (usage errors print text) says what it printed
        if (said.Length == 0) said = string.Join(" ", Lines(document, "messages").Concat(Lines(document, "errors")).Take(5));
        var temporary = said.Contains("DDB-242", StringComparison.Ordinal);   // the database refused or lost a command: the connection may be back
        return new TaskFailureException(temporary ? TaskFailureKind.Temporary : TaskFailureKind.Permanent, $"`connection refresh {Input.Connection} {Input.Plan}` ended with exit code {exit} and ran no load. {said}".TrimEnd());
    }
}
