using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.State;

namespace DbDataBuild.Cli.Runner;

/// <summary>What the preflight is started with: the connections whose status is read, comma separated (the default the manifest gives is every connection the plans use).</summary>
public sealed record PreflightInput(string Connections = "");

/// <summary>
/// The refresh package's preflight (Design: docs/publishing.md#preflight-before-activation): the status of each connection the plans use (`connection status`, read only, with the read logins), turned into findings a control service
/// reads before it serves the version. One step per connection, keyed by it, each with the command's document as its detail. A structure that is not deployed, is behind the project or has a blocked deploy step is an error, because a
/// refresh of this version is refused until the structure is deployed; an object changed outside the tool is a warning; what the command noticed is information.
/// </summary>
internal static class BuildPreflight
{
    private static readonly Regex Word = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The task's input as the manifest declares it, with every connection of <paramref name="connections"/> as its default.</summary>
    public static TaskInput InputOf(IEnumerable<string> connections) =>
        new("connections", DbDataKit.State.ProgressType.Text, Default: string.Join(",", connections), Description: "The connections whose status is read, comma separated (the default: every connection the plans use)");

    /// <summary>The connections named, or the refusal of a name that is not one.</summary>
    public static (IReadOnlyList<string> Connections, string? Refusal) Connections(string text)
    {
        var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0) return ([], "`connections` names no connection.");
        return names.FirstOrDefault(n => !Word.IsMatch(n)) is { } bad ? ([], $"`connections` names `{bad}`, which is not a connection's name.") : (names, null);
    }

    /// <summary>The findings of `connection status <paramref name="connection"/> --format json` that ended with <paramref name="exit"/>.</summary>
    public static IReadOnlyList<PreflightFinding> Findings(string connection, int exit, JsonDocument document)
    {
        var found = new List<PreflightFinding>();
        var root = document.RootElement;
        if (exit is not (CliApp.ExitOk or CliApp.ExitFindings))
        {
            var said = RefreshJob.Describe(document);
            if (said.Length == 0) said = string.Join(" ", RefreshJob.Lines(document, "messages").Concat(RefreshJob.Lines(document, "errors")).TakeLast(5));
            found.Add(new(Preflight.Error, $"`connection status {connection}` ended with exit code {exit}, so where the connection stands is not known. {said}".TrimEnd(), connection));
            return found;
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Array)
            foreach (var d in diagnostics.EnumerateArray())
            {
                string Text(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var severity = Text("severity") switch { "error" => Preflight.Error, "warning" => Preflight.Warning, _ => Preflight.Info };
                found.Add(new(severity, $"{Text("code")}: {Text("title")}{(Text("found") is { Length: > 0 } f ? $" ({f})" : "")}", connection));
            }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
                foreach (var o in objects.EnumerateArray())
                {
                    var name = o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    switch (o.TryGetProperty("state", out var s) ? s.GetString() : null)
                    {
                        case "missing": found.Add(new(Preflight.Error, $"{name} is not on the connection: deploy the project's structure (apply_plan) before this version refreshes it.", connection)); break;
                        case "out_of_band": found.Add(new(Preflight.Warning, $"{name} was changed outside dbdatabuild since it was deployed.", connection)); break;
                    }
                }

            if (data.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Object && preview.TryGetProperty("blocked", out var blocked) && blocked.ValueKind == JsonValueKind.Array)
                foreach (var b in blocked.EnumerateArray()) found.Add(new(Preflight.Error, $"A deploy of the structure is blocked: {b.GetString()}", connection));

            if (data.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Object && summary.TryGetProperty("structure", out var structure) && structure.GetString() == "pending")
            {
                int Count(string name) => summary.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                found.Add(new(Preflight.Error, $"The deployed structure is behind the project: {Count("steps")} deploy step(s) are pending ({Count("risky")} risky, {Count("destructive")} destructive), and a refresh is refused until they are applied (apply_plan).", connection));
            }

            if (data.TryGetProperty("noticed", out var noticed) && noticed.ValueKind == JsonValueKind.Array)
                foreach (var line in noticed.EnumerateArray()) if (line.GetString() is { Length: > 0 } text) found.Add(new(Preflight.Info, text, connection));
        }

        if (!found.Any(f => f.Severity != Preflight.Info)) found.Insert(0, new(Preflight.Info, "The deployed structure is in step with the project.", connection));
        return found;
    }

    /// <summary>The step each connection's findings are recorded under, keyed by the part (<c>status &lt;connection&gt;</c>).</summary>
    public static readonly ValueStep<JsonObject, JsonObject> Result = Step.Value<JsonObject, JsonObject>(Preflight.StepName, r => (string?)r["part"] ?? "preflight");
}

/// <summary>Reads the status of each connection the plans use and records its findings (<see cref="BuildPreflight"/>); a finding is the document's to say, not a failure of the run.</summary>
[RunnerTask(Preflight.TaskName)]
public sealed class PreflightJob : RunnerJob<PreflightInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var project = Environment.GetEnvironmentVariable(RefreshJob.PackageDirectoryVariable);
        if (string.IsNullOrWhiteSpace(project) || !Directory.Exists(project))
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The package directory ({RefreshJob.PackageDirectoryVariable}) is not set or does not exist; the preflight runs from the project the package holds.");
        var (connections, refusal) = BuildPreflight.Connections(Input.Connections);
        if (refusal is not null) throw new TaskFailureException(TaskFailureKind.Permanent, $"{Preflight.TaskName}: {refusal}");
        if (Run.DryRun)
        {
            Run.WouldDo($"read the status of {string.Join(", ", connections)} (connection status); nothing is written");
            return;
        }

        foreach (var connection in connections)
        {
            Status($"{Preflight.TaskName} {connection}");
            var args = BuildViews.Arguments(BuildViews.Status, new Dictionary<string, string> { ["connection"] = connection }, project);
            var (exit, document) = await System.Threading.Tasks.Task.Run(() => RefreshJob.Invoke(args, Environment.GetEnvironmentVariable), ct).ConfigureAwait(false);
            using var _ = document;
            var recorded = Preflight.Document($"status {connection}", BuildPreflight.Findings(connection, exit, document), JsonNode.Parse(document.RootElement.GetRawText()));
            await Step(BuildPreflight.Result, recorded).Repeatable().RunAsync(_ => Task.FromResult(recorded), ct).ConfigureAwait(false);
        }
    }
}
