using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.State;

namespace DbDataBuild.Cli.Runner;

/// <summary>What the tests task is started with: the tests to run (names or files, comma separated; blank: every test), their tags and their kind.</summary>
public sealed record TestsViewInput(string Tests = "", string Tags = "", string Kind = "");

/// <summary>What the status task is started with: the connection, and a refresh plan to see only that plan's line.</summary>
public sealed record StatusViewInput(string Connection, string Plan = "");

/// <summary>What the compare task is started with: the connection and table, and what it is compared with (another table, schema or connection), the key and whether values are read.</summary>
public sealed record CompareViewInput(string Connection, string Table, string Against = "", string AgainstSchema = "", string AgainstConnection = "", string Key = "", bool ShowValues = false);

/// <summary>
/// The database-facing views of a Build project as read-only manual tasks of its refresh package (plan 33 SC20p): the project's tests (`project tests run`), the status of a connection (`connection status`) and compare
/// (`connection compare`). Each runs the command of the same name through <see cref="CliApp"/> in the task process, as <see cref="OperationJob{TInput}"/> does for `apply_plan`, with the read logins the runner's slots fill, and records
/// the command's JSON document as the step <see cref="StepName"/> of the run, which the Workspace's Runs tab reads back. Nothing is written to a target. Task names have no hyphen, as the operations' have not, so no refresh plan
/// (`connection-plan`) is taken for one.
/// </summary>
internal static class BuildViews
{
    public const string Tests = "tests";
    public const string Status = "status";
    public const string Compare = "compare";

    /// <summary>The step a view's document is recorded under.</summary>
    public const string StepName = "view-result";

    /// <summary>The largest document recorded whole; a larger one is recorded as its head (the step's cap is 64 KiB).</summary>
    public const int MaxDocumentBytes = 60 * 1024;

    public static IReadOnlyList<string> All { get; } = [Tests, Status, Compare];

    private static readonly Regex Word = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Name = new(@"^[A-Za-z0-9_][A-Za-z0-9_.$/ -]{0,255}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The inputs of a view as the manifest declares them.</summary>
    public static IReadOnlyList<TaskInput> InputsOf(string task) => task switch
    {
        Tests =>
        [
            new("tests", ProgressType.Text, Default: "", Description: "The tests to run, comma separated (tests/metadata/naming/x.sql is naming.x); blank: every test"),
            new("tags", ProgressType.Text, Default: "", Description: "Run only tests with one of these tags, comma separated"),
            new("kind", ProgressType.Text, Default: "", Description: "metadata, model or blank (both)"),
        ],
        Status =>
        [
            new("connection", ProgressType.Text, Required: true, Description: "The connection whose state is read"),
            new("plan", ProgressType.Text, Default: "", Description: "A refresh plan, to see only that plan's line"),
        ],
        Compare =>
        [
            new("connection", ProgressType.Text, Required: true, Description: "The connection of the table"),
            new("table", ProgressType.Text, Required: true, Description: "The table or view, as schema_name.table_name"),
            new("against", ProgressType.Text, Default: "", Description: "The table to compare it with, as schema_name.table_name"),
            new("against_schema", ProgressType.Text, Default: "", Description: "Compare with the table of the same name under this schema name"),
            new("against_connection", ProgressType.Text, Default: "", Description: "Compare with the table on this connection"),
            new("key", ProgressType.Text, Default: "", Description: "The columns that identify a row, comma separated (blank: the model's grain)"),
            new("show_values", ProgressType.Bool, Default: "false", Description: "Read values: the smallest and largest of each column and sample rows of each difference"),
        ],
        _ => [],
    };

    private static IEnumerable<string> Split(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The refusal of inputs no command should be given (an option in the place of a name), or null. Checked by the task before it runs.</summary>
    public static string? Refusal(string task, IReadOnlyDictionary<string, string> inputs)
    {
        string Get(string name) => inputs.TryGetValue(name, out var v) ? v : "";
        switch (task)
        {
            case Tests:
                foreach (var test in Split(Get("tests"))) if (!Name.IsMatch(test)) return $"`tests` names `{test}`, which is not a test.";
                foreach (var tag in Split(Get("tags"))) if (!Word.IsMatch(tag)) return $"`tags` names `{tag}`, which is not a tag.";
                if (Get("kind") is not ("" or "metadata" or "model")) return "`kind` is metadata, model or blank.";
                return null;
            case Status:
                if (!Word.IsMatch(Get("connection"))) return "`connection` is a connection's name.";
                if (Get("plan").Length > 0 && !Word.IsMatch(Get("plan"))) return "`plan` is a refresh plan's name.";
                return null;
            case Compare:
                if (!Word.IsMatch(Get("connection"))) return "`connection` is a connection's name.";
                if (!Name.IsMatch(Get("table"))) return "`table` is schema_name.table_name.";
                if (Get("against").Length > 0 && !Name.IsMatch(Get("against"))) return "`against` is schema_name.table_name.";
                if (Get("against_schema").Length > 0 && !Word.IsMatch(Get("against_schema"))) return "`against_schema` is a schema's name.";
                if (Get("against_connection").Length > 0 && !Word.IsMatch(Get("against_connection"))) return "`against_connection` is a connection's name.";
                foreach (var column in Split(Get("key"))) if (!Name.IsMatch(column)) return $"`key` names `{column}`, which is not a column.";
                return null;
            default:
                return $"`{task}` is not a view (tests, status, compare).";
        }
    }

    /// <summary>The command line of the view: `project tests run`, `connection status`, `connection compare`, always `--format json`.</summary>
    public static string[] Arguments(string task, IReadOnlyDictionary<string, string> inputs, string project)
    {
        string Get(string name) => inputs.TryGetValue(name, out var v) ? v : "";
        var args = new List<string>();
        switch (task)
        {
            case Tests:
                args.AddRange(["project", "tests", "run"]);
                args.AddRange(Split(Get("tests")));
                foreach (var tag in Split(Get("tags"))) { args.Add("--tag"); args.Add(tag); }
                if (Get("kind").Length > 0) { args.Add("--kind"); args.Add(Get("kind")); }
                break;
            case Status:
                args.AddRange(["connection", "status", Get("connection")]);
                if (Get("plan").Length > 0) args.Add(Get("plan"));
                break;
            default:
                args.AddRange(["connection", "compare", Get("connection"), Get("table")]);
                if (Get("against").Length > 0) { args.Add("--against"); args.Add(Get("against")); }
                if (Get("against_schema").Length > 0) { args.Add("--against-schema"); args.Add(Get("against_schema")); }
                if (Get("against_connection").Length > 0) { args.Add("--against-connection"); args.Add(Get("against_connection")); }
                if (Split(Get("key")).ToList() is { Count: > 0 } key) { args.Add("--key"); args.AddRange(key); }
                if (bool.TryParse(Get("show_values"), out var values) && values) args.Add("--show-values");
                break;
        }

        args.AddRange(["--project", project, "--format", "json"]);
        return [.. args];
    }

    /// <summary>The document as it is recorded: whole when it fits, else its head with <c>truncated</c> and its size, so a step never fails for the size of what a view said.</summary>
    public static JsonObject Recorded(string task, int exit, JsonDocument document)
    {
        var text = document.RootElement.GetRawText();
        var record = new JsonObject { ["view"] = task, ["exit_code"] = exit };
        if (System.Text.Encoding.UTF8.GetByteCount(text) <= MaxDocumentBytes) record["document"] = JsonNode.Parse(text);
        else
        {
            record["truncated"] = true;
            record["bytes"] = System.Text.Encoding.UTF8.GetByteCount(text);
            record["head"] = text[..Math.Min(text.Length, MaxDocumentBytes / 2)];
        }

        return record;
    }

    /// <summary>The step a view's document is recorded under, keyed by the view.</summary>
    public static readonly ValueStep<JsonObject, JsonObject> Result = Step.Value<JsonObject, JsonObject>(StepName, r => (string?)r["view"] ?? "view");
}

/// <summary>The shared run of the three views: the command in this process with the read logins the slots fill, its document recorded as the run's step; a refusal or a finding is the document's to say, not a failure.</summary>
public abstract class ViewJob<TInput> : RunnerJob<TInput, NoProgress, NoSlots>
{
    internal abstract string View { get; }

    protected override async Task RunAsync(CancellationToken ct)
    {
        var project = Environment.GetEnvironmentVariable(RefreshJob.PackageDirectoryVariable);
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The package directory ({RefreshJob.PackageDirectoryVariable}) is not set or does not exist; a view runs from the project the package holds.");

        var inputs = InputBinder.ToStrings(Input);
        if (BuildViews.Refusal(View, inputs) is { } refusal) throw new TaskFailureException(TaskFailureKind.Permanent, $"{View}: {refusal}");
        if (Run.DryRun)
        {
            Run.WouldDo($"read {View} of the project ({string.Join(' ', BuildViews.Arguments(View, inputs, "<package>").Take(4))}); nothing is written");
            return;
        }

        var args = BuildViews.Arguments(View, inputs, project);
        Status($"{View} {(inputs.TryGetValue("connection", out var c) ? c : "")}".TrimEnd());
        var (exit, document) = await System.Threading.Tasks.Task.Run(() => RefreshJob.Invoke(args, Environment.GetEnvironmentVariable), ct).ConfigureAwait(false);
        using var _ = document;
        var recorded = BuildViews.Recorded(View, exit, document);
        await Step(BuildViews.Result, recorded).Repeatable().RunAsync(_ => Task.FromResult(recorded), ct).ConfigureAwait(false);
        // findings (drift, a failing test, rows that differ) are what a view is asked for: the document says them; a command that could not run at all fails the run
        if (exit is not (CliApp.ExitOk or CliApp.ExitFindings))
        {
            var said = RefreshJob.Describe(document);
            if (said.Length == 0) said = string.Join(" ", RefreshJob.Lines(document, "messages").Concat(RefreshJob.Lines(document, "errors")).TakeLast(5));
            throw new TaskFailureException(exit == CliApp.ExitBusy ? TaskFailureKind.Temporary : TaskFailureKind.Permanent, $"`{string.Join(' ', args.TakeWhile(a => a != "--project"))}` ended with exit code {exit}. {said}".TrimEnd());
        }
    }
}

/// <summary>Runs the project's tests (`project tests run`) on the runner and records their document (plan 33 SC20p).</summary>
[RunnerTask(BuildViews.Tests)]
public sealed class TestsViewJob : ViewJob<TestsViewInput>
{
    internal override string View => BuildViews.Tests;
}

/// <summary>Reads where a connection stands (`connection status`) with the read login and records the document (plan 33 SC20p).</summary>
[RunnerTask(BuildViews.Status)]
public sealed class StatusViewJob : ViewJob<StatusViewInput>
{
    internal override string View => BuildViews.Status;
}

/// <summary>Compares two tables (`connection compare`) with the read logins and records the document (plan 33 SC20p).</summary>
[RunnerTask(BuildViews.Compare)]
public sealed class CompareViewJob : ViewJob<CompareViewInput>
{
    internal override string View => BuildViews.Compare;
}
