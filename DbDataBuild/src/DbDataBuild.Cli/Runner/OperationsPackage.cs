using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataBuild.Cli.Runner;

/// <summary>
/// A project's person-driven writes as a definition package of their own (plan 28c E21): the tasks `apply_plan`, `ack` and `seed` of <see cref="BuildOperations"/>, every one manual-only (no schedule, no trigger but a person's
/// hand) and holding a slot for every connection of the project, so the runner's binding of the channel is where the write login comes from. It is a package apart from the refresh package (<see cref="RunnerProject"/>)
/// because it carries what the refresh package leaves out on purpose: the project's sources (`models/`, `seeds/`, `macros/`, `hooks/`, `views/`), which `ack definition` and `seed` read; the refresh package holds only what was compiled.
/// It holds no plan and no secret: a plan is kept in the tracking tables or travels as an input of the run.
/// </summary>
internal static class OperationsPackage
{
    /// <summary>The folders of the project's sources a package carries, beside the project file and the connection files.</summary>
    public static readonly string[] SourceFolders = ["models", "seeds", "macros", "hooks", "views"];

    /// <summary>How long each task may run: an apply may rebuild large tables; an acknowledgement is one row.</summary>
    private static TimeSpan TimeoutOf(string task) => task switch { BuildOperations.Ack => TimeSpan.FromMinutes(15), _ => TimeSpan.FromHours(6) };

    /// <summary>The package of the project in <paramref name="root"/>, or null with the reasons in <paramref name="diagnostics"/>.</summary>
    public static BuiltPackage? Build(string root, string package, string runtimeRange, List<Diagnostic> diagnostics)
    {
        var before = diagnostics.Count;
        if (!PackageManifestValidator.NameRule.IsMatch(package))
        {
            diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new("--package", 0, 0), $"`{package}` is not a package name: 1 to 64 lower-case letters, digits, `-` or `_`, starting with a letter or digit."));
            return null;
        }

        var configDiagnostics = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiagnostics);
        if (configDiagnostics.Any(d => d.Severity == Severity.Error)) { diagnostics.AddRange(configDiagnostics.Where(d => d.Severity == Severity.Error)); return null; }

        var slots = new List<ConnectionSlot>();
        foreach (var name in config.Connections.Keys.Order(StringComparer.Ordinal))
        {
            if (config.EngineOf(name) is not { Length: > 0 } engine) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new($"connections/{name}.yaml", 0, 0), $"The connection `{name}` has no engine.")); continue; }
            if (!PackageManifestValidator.IdentifierRule.IsMatch(name)) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new($"connections/{name}.yaml", 0, 0), $"The connection `{name}` is a slot of the package, and a slot's name is lower-case letters, digits and `_`, starting with a letter, at most 64.")); continue; }
            slots.Add(new ConnectionSlot(name, engine, SlotEnvironmentFormats.BuildLogin));
        }

        if (slots.Count == 0 && diagnostics.Count == before)
            diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(ProductInfo.ConfigFile, 0, 0), "The project has no connection, so there is nothing for an operation to write to."));
        if (diagnostics.Count > before) return null;

        var tasks = BuildOperations.All.Select(task => new PackageTask(task, new TaskEntry(), new Schedule(), [TaskTrigger.ByHand()], BuildOperations.InputsOf(task), slots, ProgressDeclaration.Empty,
            new TaskLimits(Timeout: TimeoutOf(task), Concurrency: 1), [], [])).ToList();
        var manifest = new PackageManifest(package, $"The dbdatabuild project {package}: the writes a person starts (apply a plan, acknowledge a block, seed), manual only",
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(RunnerProject.RuntimeName, runtimeRange)),
            new BuiltAgainst($"{Environment.Version.Major}.{Environment.Version.Minor}", null, RunnerProtocol.V1), tasks);
        var text = PackageManifestWriter.Write(manifest);
        var load = PackageManifestReader.Load(text, "package.yaml");
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error)) diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new("package.yaml", 0, 0), d.ToString()));
            return null;
        }

        return new BuiltPackage(package, manifest, text, Files(root), []);
    }

    private static List<PackageFile> Files(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string relative, string full) { if (File.Exists(full)) files[relative] = full; }
        Add(ProductInfo.ConfigFile, Path.Combine(root, ProductInfo.ConfigFile));
        Add(ProductInfo.DialectLockFile, Path.Combine(root, ProductInfo.DialectLockFile));
        foreach (var folder in new[] { "connections" }.Concat(SourceFolders))
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) files[Path.GetRelativePath(root, path).Replace('\\', '/')] = path;
        }

        return [.. files.Select(f => new PackageFile(f.Key, f.Value))];
    }
}
