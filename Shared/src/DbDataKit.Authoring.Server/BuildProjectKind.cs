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

    // the package of refresh plans goes to a Runner (SC20d); the compiled semantic models go to a Gateway (SC20e) over data sources the Gateway already has: a data source carries a login, the Workspace holds none, so no data source is made from here
    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } =
        [new("runner", "package", "The project's refresh plans (the committed rendered/ files) as a definition package: one task per plan with its schedule and connection slots, staged on a channel of a Runner."),
         new("gateway", "semantic-model", "The project's compiled semantic models (semantic/), published to a Gateway over the data sources it already has under the connections' names. No login is sent: the Workspace holds none.")];

    public bool Produces => true;

    /// <summary>
    /// The package of <c>dbdatabuild project publish --to runner --out</c> (plan 28c E07): the committed files of the project, nothing compiled, nothing sent. A project with no <c>rendered/</c> plan, or one whose plan is refused (a bad cron, a file that
    /// changed after the plan hashed it) answers with the program's own words, which name the file.
    /// </summary>
    public async Task<ProjectArtifact> ProduceAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken)
    {
        // each thing the project has is made, and each that is made must succeed: the package of the committed plans (rendered/) for a Runner, the models (semantic/) for a Gateway (SC20e); a project with neither gets the package's own words
        var hasModels = Directory.Exists(Path.Combine(root, "semantic")) && Directory.EnumerateFiles(Path.Combine(root, "semantic"), "*.yml", SearchOption.AllDirectories).Any();
        var hasPlans = Directory.Exists(Path.Combine(root, "rendered"));
        ProjectArtifact? package = null, models = null;
        if (hasPlans || !hasModels) package = await ProducePackageAsync(root, projectId, outputFolder, cancellationToken);
        if (hasModels && package is not { Succeeded: false }) models = await ProduceModelsAsync(root, Path.Combine(outputFolder, "gateway"), cancellationToken);
        if (package is { Succeeded: false }) return package;
        if (models is { Succeeded: false }) return models;
        var info = new Dictionary<string, string>();
        foreach (var part in new[] { package, models })
            if (part?.Info is not null) foreach (var (k, v) in part.Info) info[k] = v;
        return new(true, string.Join(" ", new[] { package?.Message, models?.Message }.Where(m => m is not null)), [], info);
    }

    /// <summary>The models a Gateway publish sends (<c>dbdatabuild project publish --to gateway --out</c>, SC20e): no database login is read, nothing is sent.</summary>
    private async Task<ProjectArtifact> ProduceModelsAsync(string root, string folder, CancellationToken cancellationToken)
    {
        (int Exit, string Output, string Error) result;
        try { result = await tool.RunAsync(["project", "publish", "--to", "gateway", "--out", folder, "--project", root], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return new(false, ex.Message, [new("error", "DBD-WS-BUILD-MISSING", ex.Message)]); }
        if (result.Exit != 0)
        {
            var said = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
            var message = said.Length > 0 ? said : $"dbdatabuild stopped with exit code {result.Exit}.";
            return new(false, message, [new("error", "DBD-WS-BUILD-PUBLISH", message)]);
        }
        if (!File.Exists(Path.Combine(folder, "gateway.json")))
            return new(false, "The program said it wrote the models but gateway.json is not there.", [new("error", "DBD-WS-PACKAGE", "The program said it wrote the models but gateway.json is not there.")]);
        var models = new List<string>();
        var dataSources = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "gateway.json")));
            if (document.RootElement.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array)
                models.AddRange(m.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(x => x.Length > 0));
            if (document.RootElement.TryGetProperty("data_sources", out var d) && d.ValueKind == JsonValueKind.Array)
                dataSources.AddRange(d.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Where(x => x.Length > 0));
        }
        catch (JsonException) { return new(false, "gateway.json of the models is not a document.", [new("error", "DBD-WS-PACKAGE", "gateway.json of the models is not a document.")]); }
        return new(true, $"The models: {string.Join(", ", models)}.", [], new Dictionary<string, string> { ["models"] = string.Join(", ", models), ["data_sources"] = string.Join(", ", dataSources) });
    }

    private async Task<ProjectArtifact> ProducePackageAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken)
    {
        var package = PackageArtifacts.NameOf(projectId);
        (int Exit, string Output, string Error) result;
        try { result = await tool.RunAsync(["project", "publish", "--to", "runner", "--out", outputFolder, "--package", package, "--project", root], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return new(false, ex.Message, [new("error", "DBD-WS-BUILD-MISSING", ex.Message)]); }
        if (result.Exit != 0)
        {
            var said = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
            var message = said.Length > 0 ? said : $"dbdatabuild stopped with exit code {result.Exit}.";
            return new(false, message, [new("error", "DBD-WS-BUILD-PUBLISH", message)]);
        }
        return PackageArtifacts.FromOutput(package, outputFolder, result.Output, "tasks");
    }

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
