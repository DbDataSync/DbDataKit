using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>The program of the Build product the Workspace runs as a child process (<see cref="ChildProgram"/>): <c>dbdatabuild</c>, which prints one JSON document with <c>--format json</c>.</summary>
public sealed class BuildTool(string? executable = null, TimeSpan? timeout = null) : ChildProgram("dbdatabuild", executable, timeout, Missing, ["--format", "json"])
{
    public const string Missing = "This server cannot work on a Build project: the program dbdatabuild is not beside it or on its PATH. Install Build (the suite package has it) and restart the Workspace server.";
}

/// <summary>
/// The <c>build</c> kind (plan 33 SC20c): a Build project, the models, seeds, tests and connections of <c>dbdatabuild</c>, authored as files in a git workspace. A new project starts as Build's own <c>starter</c> template (made by
/// <c>dbdatabuild project create</c>, so there is one source of the template), and a draft is checked by <c>dbdatabuild project show metadata</c>, which loads and validates the whole project offline and writes nothing; its diagnostics
/// (<c>DDB-</c> codes with file and line) are the ones Build's own screens show. It is checked, not built: what it produces is published (SC20d, SC20e).
/// </summary>
public sealed class BuildProjectKind(BuildTool tool) : IProjectKind
{
    public string Name => "build";
    public string Title => "Build project";
    public string Description => "Models, seeds, tests and connections of dbdatabuild: tables and views built from DuckDB-dialect SQL. Checked with Build's own validators; published to a Runner as a package and to a Gateway as models.";
    public bool Buildable => false;
    public string? GitIgnore => null;       // the template carries its own .gitignore

    // SC20d adds the package to a Runner; a route is listed only when it can be used
    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } = [];

    private static readonly UTF8Encoding Utf8 = new(false, true);

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title)
    {
        var folder = Path.Combine(Path.GetTempPath(), "ddw-build-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, _, error) = tool.RunAsync(["project", "create", "starter", folder], Path.GetTempPath(), CancellationToken.None).GetAwaiter().GetResult();
            if (exit != 0 || !Directory.Exists(folder)) throw new ProjectKindUnavailableException($"dbdatabuild could not make the starter project (exit {exit}): {error.Trim()}");
            var files = new List<ProjectKindFile>();
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                var bytes = File.ReadAllBytes(file);
                if (relative == "dbdatabuild.yml" && title is { Length: > 0 })
                    bytes = Utf8.GetBytes(Regex.Replace(Utf8.GetString(bytes), "(?m)^title:.*$", "title: " + JsonSerializer.Serialize(title), RegexOptions.None, TimeSpan.FromSeconds(2)));
                files.Add(new ProjectKindFile(relative, bytes));
            }
            return files;
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken)
    {
        if (tool.Find() is null) return [new("error", "DBD-WS-BUILD-MISSING", BuildTool.Missing)];
        (int Exit, string Output, string Error) result;
        try { result = await tool.RunAsync(["project", "show", "metadata"], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return [new("error", "DBD-WS-BUILD-MISSING", ex.Message)]; }
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var found = new List<ProjectDiagnostic>();
            if (document.RootElement.TryGetProperty("diagnostics", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var d in list.EnumerateArray())
                {
                    var severity = Text(d, "severity") is "note" ? "info" : Text(d, "severity") ?? "error";
                    var location = d.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
                    var message = Text(d, "title") is { } title && Text(d, "found") is { Length: > 0 } what ? $"{title}: {what}" : Text(d, "title") ?? Text(d, "found") ?? "A finding without a message.";
                    if (Text(d, "fix") is { Length: > 0 } fix) message += " " + fix;
                    found.Add(new ProjectDiagnostic(severity, Text(d, "code") ?? "DDB-000", message, location.ValueKind == JsonValueKind.Object ? Text(location, "file") : null,
                        location.ValueKind == JsonValueKind.Object && location.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? line.GetInt32() : null));
                }
            // an exit with findings is 1; anything else that is not 0 and left no diagnostic is the tool failing, which is said, never passed over
            if (result.Exit is not (0 or 1) && !found.Any(f => f.IsError))
                found.Add(new("error", "DBD-WS-BUILD-FAILED", $"dbdatabuild stopped with exit code {result.Exit}: {result.Error.Trim()}"));
            return found;
        }
        catch (JsonException)
        {
            return [new("error", "DBD-WS-BUILD-FAILED", $"dbdatabuild did not answer with a document (exit code {result.Exit}): {result.Error.Trim()}")];
        }

        static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
