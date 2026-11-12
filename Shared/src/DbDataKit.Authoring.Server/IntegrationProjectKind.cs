using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;
using DbDataKit.Config.Yaml;
using DbDataKit.Runner;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The program of the Runner product the Workspace runs as a child process (<see cref="ChildProgram"/>) to build and test a csx integration: <c>dbdatarunner package build|test</c>, which needs only the .NET runtime (docs/integrations.md section 7).
/// <see cref="Sources"/> and <see cref="Cache"/> are where it finds NuGet packages and keeps them; the first is a v3 index address or a folder per entry, nuget.org when none.
/// </summary>
public sealed class RunnerTool(string? executable = null, TimeSpan? timeout = null, IReadOnlyList<string>? sources = null, string? cache = null)
    : ChildProgram("dbdatarunner", executable, timeout ?? TimeSpan.FromMinutes(5), Missing, ["--format", "json"])
{
    public IReadOnlyList<string> Sources { get; } = sources ?? [];

    public string? Cache { get; } = string.IsNullOrWhiteSpace(cache) ? null : cache;

    public const string Missing = "This server cannot build an integration: the program dbdatarunner is not beside it or on its PATH. Install the Runner (the suite package has it) and restart the Workspace server.";

    /// <summary>The options every <c>package build</c> and <c>package test</c> takes for the packages: a source each, and the cache.</summary>
    internal IEnumerable<string> PackageOptions() => [.. Sources.SelectMany(s => new[] { "--source", s }), .. Cache is null ? [] : new[] { "--cache", Cache }];
}

/// <summary>The .NET SDK's <c>dotnet</c> program, which only a compiled integration project needs (<c>dotnet publish</c>). A machine with only the runtime has a <c>dotnet</c> that lists no SDK, and that is the answer, not a failure of the build.</summary>
public sealed class DotnetTool(string? executable = null, TimeSpan? timeout = null) : ChildProgram("dotnet", executable, timeout ?? TimeSpan.FromMinutes(10), Missing, [])
{
    public const string Missing = "This integration is a compiled .NET project, and building it needs the .NET SDK, which is not installed on this server (only the runtime is). Install the .NET SDK there and build again, "
        + "or write the integration as C# files under src/ (csx), which this server builds without the SDK.";

    /// <summary>True when <c>dotnet --list-sdks</c> names at least one SDK.</summary>
    public async Task<bool> HasSdkAsync(CancellationToken cancellationToken)
    {
        if (Find() is null) return false;
        try
        {
            var (exit, output, _) = await RunAsync(["--list-sdks"], Path.GetTempPath(), cancellationToken);
            return exit == 0 && output.Trim().Length > 0;
        }
        catch (ProjectKindUnavailableException) { return false; }
    }
}

/// <summary>
/// The <c>integration</c> kind (plan 27a IX20, docs/integrations.md): an integration for the Runner authored as files in a git workspace, in one of two shapes. <b>csx</b>: <c>package.yaml</c> (the <c>package/1</c> manifest), the job classes in
/// <c>src/*.csx</c> with <c>#r "nuget: Id, 1.2.3"</c> lines, test scripts in <c>tests/*.csx</c>; checked, tested and built by <c>dbdatarunner package build|test</c> as a child process, with no .NET SDK. <b>Compiled</b>: a <c>.csproj</c> in the root or
/// in <c>src/</c>, built by <c>dotnet publish</c>, which needs the SDK; a server without one says so. The draft is checked offline into a scratch folder and nothing of it is kept; a build runs the tests first (a failing test fails the build) and then
/// writes the package (<c>package.yaml</c> and <c>files/</c>) that is published to a Runner with the existing publish path.
/// </summary>
public sealed partial class IntegrationProjectKind(RunnerTool runner, DotnetTool dotnet) : IProjectKind
{
    public string Name => "integration";
    public string Title => "Integration";
    public string Description => "Code that moves data between the suite and an outside system, run by a Runner on a schedule or a trigger: a manifest of tasks and the job classes (C# files with NuGet references, built with no SDK; or a compiled .NET project), with tests. Built and tested here; published to a Runner as a package.";
    public bool Buildable => false;
    public string? GitIgnore => "bin/\nobj/\n.build/\n";

    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } =
        [new("runner", "package", "The integration as a package: its manifest and built assemblies, staged on a channel of a Runner, which runs its tasks from the connections bound there.")];

    public bool Produces => true;

    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>The starting points beside the plain one (plan 27a IX21). They generate ordinary code, which the author then owns: there is no engine behind a template and nothing keeps a project tied to it.</summary>
    public IReadOnlyList<ProjectTemplate> Templates { get; } =
    [
        new("rest-api-to-staging", "Pull pages from a REST API into a staging table", "A task that reads an API page by page (cursor paging, retries, a rate limit) and lands each page in a staging table once, with the API's answers recorded for tests that need no network."),
        new("csv-files-from-share", "Pick up CSV files from a share", "A task that picks up the finished CSV files of a folder, lands their rows in a staging table once per content and moves them aside, with a fake share for tests that need no share."),
    ];

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title, string? template)
    {
        if (template is null) return Template(id, title);
        if (Templates.All(t => t.Name != template)) throw new ArgumentException($"The integration kind has no template called '{template}'.", nameof(template));
        return IntegrationTemplates.Files(template, id, title);
    }

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title)
    {
        var package = PackageArtifacts.NameOf(id);
        static ProjectKindFile File(string path, string text) => new(path, Utf8.GetBytes(text.Replace("\r\n", "\n")));
        var manifest = string.Join("\n",
            "schema: package/1", $"name: {package}", $"launch: {{ kind: assembly, assembly: bin/{package}.dll }}",
            "built_against: { dotnet: \"10.0\", kit: \"1\", protocol: dbdatakit.runner.v1 }",
            "tasks:", "  - name: hello", "    entry: { type: Integration.Hello }", "    triggers:", "      - manual: true", "");
        var job = string.Join("\n",
            "// One job class per task of package.yaml: [RunnerTask(\"<task name>\")] on a class of RunnerJob<TInput, TProgress, TSlots>.",
            "// Packages: #r \"nuget: Id, 1.2.3\" (an exact version) at the top of a file; the first build pins them in package.lock.",
            "namespace Integration;", "",
            "public static class HelloCounters { public static readonly Counter Greetings = new(\"greetings\"); }", "",
            "[RunnerTask(\"hello\")]", "public sealed class Hello : RunnerJob<NoInput, NoProgress, NoSlots>", "{",
            "    protected override Task RunAsync(CancellationToken ct)", "    {", "        Count(HelloCounters.Greetings, 1);", "        return Task.CompletedTask;", "    }", "}", "");
        var test = string.Join("\n",
            "// A test is a script: it passes when it finishes and fails when it throws. True, Equal and Fail are in scope; Project is this folder.",
            "var sim = new RunSimulator<Integration.Hello>();", "await sim.RunAsync();",
            "True(sim.Outcome == DbDataKit.Monitoring.RunOutcome.Succeeded, \"the run did not succeed\");",
            "Equal(1L, sim.Counted(Integration.HelloCounters.Greetings));", "sim.Dispose();", "");
        var readme = $"# {title ?? id}\n\nAn integration for the Runner. `package.yaml` lists the tasks (schedule, triggers, inputs, connection slots, limits), `src/` holds the job classes, `tests/` the tests.\n"
            + "Secrets are never in a file: a connection slot is bound to a connection on the Runner, and a connection names its secret by reference (`dbdatakit:<service>:<kind>:<name>`).\n"
            + "Check, build (which runs the tests first) and publish from the Workspace; `docs/integrations.md` of the suite has the whole picture.\n";
        return [File("package.yaml", manifest), File("src/hello.csx", job), File("tests/hello.csx", test), File("README.md", readme)];
    }

    /// <summary>Which shape the draft is: a <c>.csproj</c> (root or <c>src/</c>), else C# files in <c>src/</c>, else neither.</summary>
    internal static (bool Csx, string? Project) Shape(string root)
    {
        foreach (var folder in new[] { root, Path.Combine(root, "src") })
            if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).FirstOrDefault() is { } project) return (false, project);
        var src = Path.Combine(root, "src");
        return (Directory.Exists(src) && Directory.EnumerateFiles(src, "*.csx", SearchOption.AllDirectories).Any(), null);
    }

    public async Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken)
    {
        var found = new List<ProjectDiagnostic>();
        if (!System.IO.File.Exists(Path.Combine(root, "package.yaml"))) { found.Add(new("error", "DBD-WS-INTEGRATION-MANIFEST", "package.yaml is missing: an integration is a package/1 manifest and its code.", "package.yaml")); return found; }
        var (csx, project) = Shape(root);
        if (!csx && project is null) { found.Add(new("error", "DBD-WS-INTEGRATION-NOCODE", "The project has no code: put the job classes in src/*.csx (C# files), or a .csproj in src/ for a compiled project.", "src/")); return found; }
        if (project is not null)
        {
            // a compiled project is checked as far as a check can go without compiling it: the manifest reads, and the machine can build it
            var load = PackageManifestReader.LoadFile(Path.Combine(root, "package.yaml"));
            found.AddRange(load.Diagnostics.Where(d => d.Severity != ConfigSeverity.Note).Select(d => new ProjectDiagnostic(d.Severity == ConfigSeverity.Error ? "error" : "warning", "DBD-WS-INTEGRATION-MANIFEST", d.Message, "package.yaml", d.Line > 0 ? d.Line : null)));
            if (!await dotnet.HasSdkAsync(cancellationToken)) found.Add(new("error", "DBD-WS-INTEGRATION-SDK", DotnetTool.Missing, Path.GetRelativePath(root, project).Replace('\\', '/')));
            return found;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "ddw-int-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await RunAsync(["package", "build", root, "--out", scratch, "--no-lock-write", .. runner.PackageOptions()], root, cancellationToken);
            found.AddRange(result.Diagnostics);
            if (!result.Ok && !found.Any(f => f.IsError)) found.Add(new("error", "DBD-WS-INTEGRATION-FAILED", result.Said));
        }
        finally { Cleanup(scratch); }
        return found;
    }

    public async Task<ProjectArtifact> ProduceAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken)
    {
        var package = PackageArtifacts.NameOf(projectId);
        var (csx, project) = Shape(root);
        if (!csx && project is null) return new(false, "The project has no code.", [new("error", "DBD-WS-INTEGRATION-NOCODE", "The project has no code: put the job classes in src/*.csx (C# files), or a .csproj in src/ for a compiled project.", "src/")]);
        return project is null ? await BuildCsxAsync(root, package, outputFolder, cancellationToken) : await BuildCompiledAsync(root, project, package, outputFolder, cancellationToken);
    }

    private async Task<ProjectArtifact> BuildCsxAsync(string root, string package, string outputFolder, CancellationToken cancellationToken)
    {
        // the tests first: a package whose tests fail is not made
        var tested = await RunAsync(["package", "test", root, .. runner.PackageOptions()], root, cancellationToken);
        var diagnostics = new List<ProjectDiagnostic>(tested.Diagnostics);
        var testsLine = tested.Said;
        if (!tested.Ok)
        {
            if (!diagnostics.Any(d => d.IsError)) diagnostics.Add(new("error", "DBD-WS-INTEGRATION-TEST", tested.Said));
            return new(false, tested.Said, diagnostics);
        }

        var built = await RunAsync(["package", "build", root, "--out", outputFolder, .. runner.PackageOptions()], root, cancellationToken);
        diagnostics.AddRange(built.Diagnostics.Where(d => diagnostics.All(x => x != d)));
        if (!built.Ok)
        {
            if (!diagnostics.Any(d => d.IsError)) diagnostics.Add(new("error", "DBD-WS-INTEGRATION-FAILED", built.Said));
            return new(false, built.Said, diagnostics);
        }
        var tasks = built.Data is { } data && data.TryGetProperty("tasks", out var list) && list.ValueKind == JsonValueKind.Array ? [.. list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)] : new List<string>();
        var files = built.Data is { } d2 && d2.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
        var info = new Dictionary<string, string> { ["package"] = package, ["tasks"] = string.Join(", ", tasks), ["files"] = files.ToString(System.Globalization.CultureInfo.InvariantCulture), ["tests"] = testsLine, ["language"] = "csx" };
        return new(true, $"The package {package}: {tasks.Count} task(s), {files} file(s); {testsLine}", diagnostics, info);
    }

    [GeneratedRegex(@"^(?<file>.+?)\((?<line>\d+),\d+\):\s*(?<severity>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<message>.*?)(?:\s*\[[^\]]*\])?$", RegexOptions.NonBacktracking)]
    private static partial Regex MsBuildLine();

    private async Task<ProjectArtifact> BuildCompiledAsync(string root, string project, string package, string outputFolder, CancellationToken cancellationToken)
    {
        if (!await dotnet.HasSdkAsync(cancellationToken)) return new(false, DotnetTool.Missing, [new("error", "DBD-WS-INTEGRATION-SDK", DotnetTool.Missing, Path.GetRelativePath(root, project).Replace('\\', '/'))]);
        var load = PackageManifestReader.LoadFile(Path.Combine(root, "package.yaml"));
        if (!load.Ok || load.Manifest is null)
            return new(false, "package.yaml has problems.", [.. load.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => new ProjectDiagnostic("error", "DBD-WS-INTEGRATION-MANIFEST", d.Message, "package.yaml", d.Line > 0 ? d.Line : null))]);
        var manifest = load.Manifest;
        if (manifest.Launch.Kind != LaunchKind.Assembly || manifest.Launch.Assembly is null)
            return new(false, "A compiled integration starts as an assembly.", [new("error", "DBD-WS-INTEGRATION-LAUNCH", "The launch of a compiled integration is `launch: { kind: assembly, assembly: bin/<name>.dll }`, a file the project's publish writes.", "package.yaml")]);

        var bin = Path.Combine(outputFolder, "files", "bin");
        Directory.CreateDirectory(bin);
        (int Exit, string Output, string Error) result;
        try { result = await dotnet.RunAsync(["publish", project, "-c", "Release", "-o", bin, "--nologo", "-v", "q", "-p:UseSharedCompilation=false"], root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return new(false, ex.Message, [new("error", "DBD-WS-INTEGRATION-SDK", ex.Message)]); }
        var found = new List<ProjectDiagnostic>();
        foreach (var raw in (result.Output + "\n" + result.Error).ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct())
            if (MsBuildLine().Match(raw) is { Success: true } m)
            {
                var file = m.Groups["file"].Value.Replace('\\', '/');
                var prefix = root.Replace('\\', '/').TrimEnd('/') + "/";
                if (file.StartsWith(prefix, StringComparison.Ordinal)) file = file[prefix.Length..];
                found.Add(new(m.Groups["severity"].Value, "DBD-WS-" + m.Groups["code"].Value, m.Groups["message"].Value, file, int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture)));
            }
        if (result.Exit != 0)
        {
            if (!found.Any(f => f.IsError)) found.Add(new("error", "DBD-WS-INTEGRATION-FAILED", $"dotnet publish stopped with exit code {result.Exit}: {(result.Error.Trim().Length > 0 ? result.Error.Trim() : result.Output.Trim())}"));
            return new(false, found.First(f => f.IsError).Message, found);
        }

        var launchFile = Path.Combine(outputFolder, "files", manifest.Launch.Assembly.Replace('/', Path.DirectorySeparatorChar));
        if (!System.IO.File.Exists(launchFile))
        {
            found.Add(new("error", "DBD-WS-INTEGRATION-LAUNCH", $"The publish did not write the launch assembly '{manifest.Launch.Assembly}' that package.yaml names (the files are under files/bin/).", "package.yaml"));
            return new(false, "The launch assembly is missing.", found);
        }
        System.IO.File.Copy(Path.Combine(root, "package.yaml"), Path.Combine(outputFolder, "package.yaml"), overwrite: true);
        var count = Directory.EnumerateFiles(Path.Combine(outputFolder, "files"), "*", SearchOption.AllDirectories).Count();
        var tasks = manifest.Tasks.Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        var info = new Dictionary<string, string> { ["package"] = package, ["tasks"] = string.Join(", ", tasks), ["files"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["tests"] = "not run: the tests of a compiled project are its own test project", ["language"] = "compiled" };
        return new(true, $"The package {package}: {tasks.Count} task(s), {count} file(s), built with the .NET SDK.", found, info);
    }

    private sealed record ToolResult(bool Ok, string Said, IReadOnlyList<ProjectDiagnostic> Diagnostics, JsonElement? Data);

    /// <summary>Runs the verb and reads the document it prints (<c>--format json</c>: <c>ok</c>, <c>data</c>, <c>messages</c>, <c>errors</c>); what it found, with file and line, becomes findings.</summary>
    private async Task<ToolResult> RunAsync(IReadOnlyList<string> arguments, string root, CancellationToken cancellationToken)
    {
        (int Exit, string Output, string Error) result;
        try { result = await runner.RunAsync(arguments, root, cancellationToken); }
        catch (ProjectKindUnavailableException ex) { return new(false, ex.Message, [new("error", "DBD-WS-INTEGRATION-MISSING", ex.Message)], null); }
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var top = document.RootElement.Clone();
            var found = new List<ProjectDiagnostic>();
            JsonElement? data = top.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : null;
            if (data is { } e && e.TryGetProperty("diagnostics", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var item in list.EnumerateArray())
                    found.Add(new(Text(item, "severity") ?? "error", Text(item, "code") ?? "DBD-INT", Text(item, "message") ?? "A finding without a message.", Text(item, "file"),
                        item.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? line.GetInt32() : null));
            var ok = top.TryGetProperty("ok", out var okProperty) && okProperty.ValueKind == JsonValueKind.True;
            var lines = (ok ? "messages" : "errors") is var key && top.TryGetProperty(key, out var said) && said.ValueKind == JsonValueKind.Array ? said.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            // a test verb's own lines (passed/FAILED per script) are the words of the result; a failing script is also a finding naming its file
            if (data is { } r && r.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var test in results.EnumerateArray().Where(t => t.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.False))
                    found.Add(new("error", "DBD-INT-TEST", Text(test, "message") ?? "The test failed.", Text(test, "file")));
            var sentence = lines.LastOrDefault(l => l.Length > 0) ?? (ok ? "Done." : $"dbdatarunner stopped with exit code {result.Exit}.");
            return new(ok, sentence, found, data);
        }
        catch (JsonException)
        {
            var message = $"dbdatarunner did not answer with a document (exit code {result.Exit}): {result.Error.Trim()}";
            return new(false, message, [new("error", "DBD-WS-INTEGRATION-FAILED", message)], null);
        }

        static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static void Cleanup(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* a scratch folder in the temp area */ }
    }
}
