using System.Text;
using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Planning;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataBuild.Cli.Server;

namespace DbDataBuild.Cli.Runner;

/// <summary>One file of the package: where it goes inside it and where it is now.</summary>
internal sealed record PackageFile(string Path, string Source);

/// <summary>One refresh plan as the task it becomes: its name on the runner, when it runs, and the connections it needs.</summary>
internal sealed record PublishedPlan(string Connection, string Plan, string Task, string File, string? Cron, string? TimeZone, IReadOnlyList<string> Slots, TimeSpan? Timeout);

/// <summary>A project made into a `package/1` definition package: the manifest (text and model), the files and what each plan became.</summary>
internal sealed record BuiltPackage(string Name, PackageManifest Manifest, string ManifestText, IReadOnlyList<PackageFile> Files, IReadOnlyList<PublishedPlan> Plans);

/// <summary>
/// Makes the committed files of a project into a definition package for a runner (plan 28c E07, section 4.3). It compiles nothing: it reads what `project compile` wrote (`rendered/`), checks that the committed files agree with each
/// other, and describes each refresh plan as one task. The checks are those the project would meet at run time, made before anything is sent: a plan whose schedule is not valid (DDB-106, naming the plan's file and the key), a script that
/// is not the one `loads.yml` was compiled from (DDB-448), a compiled file that does not read (DDB-446, DDB-xxx of the plan file), and a manifest the runner's own reader would refuse.
/// </summary>
internal static class RunnerProject
{
    /// <summary>The runtime package that runs a Build project (plan 28c E05): `scripts/publish-runtime.sh`'s package name.</summary>
    public const string RuntimeName = "dbdatabuild-runtime";

    /// <summary>How long beyond the sum of its loads' limits a plan with limits everywhere may take: the check before the loads, the start of the engine and the tracking writes.</summary>
    public static readonly TimeSpan TimeoutGrace = TimeSpan.FromMinutes(5);

    /// <summary>The range of runtimes a project published by this tool accepts: its minor line (`0.4.x`), which is what a project published for 0.4 reads.</summary>
    public static string RuntimeRange(string version)
    {
        var numbers = version.Split('-', '+')[0].Split('.').Take(2).ToArray();
        return numbers.Length == 2 && numbers.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)) ? $"{numbers[0]}.{numbers[1]}.x" : "*";
    }

    /// <summary>The name a project gets as a package when none is given: its folder's name, as the name rule writes it.</summary>
    public static string DefaultPackageName(string root) => NameOf(Path.GetFileName(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

    /// <summary>A name as the runner's name rule (lower-case letters, digits, `-` and `_`, starting with a letter or digit) writes it: what is not allowed becomes `-`.</summary>
    public static string NameOf(string text)
    {
        var name = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9_-]", "-").TrimStart('-', '_');
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>The task a plan becomes: `connection-plan`, in the name rule's alphabet. Two plans that come to the same name are refused by <see cref="Build"/>, naming both.</summary>
    public static string TaskName(string connection, string plan) => NameOf($"{connection}-{plan}");

    private static readonly string[] TextExtensions = [".yml", ".yaml", ".sql", ".csx", ".json", ".md", ".txt"];

    private static Diagnostic Invalid(string file, string found, string? fix = null) => new(DiagnosticCatalog.InvalidValue, new(file, 0, 0), found, Fix: fix);

    /// <summary>
    /// The package of a project, or null with the reasons in <paramref name="diagnostics"/>. <paramref name="package"/> is the package name, <paramref name="runtimeRange"/> the engines the project accepts.
    /// </summary>
    public static BuiltPackage? Build(string root, string package, string runtimeRange, List<Diagnostic> diagnostics)
    {
        var before = diagnostics.Count;
        if (!PackageManifestValidator.NameRule.IsMatch(package))
        {
            diagnostics.Add(Invalid("--package", $"`{package}` is not a package name: 1 to 64 lower-case letters, digits, `-` or `_`, starting with a letter or digit."));
            return null;
        }

        var configDiagnostics = new List<Diagnostic>();
        var config = ProjectConfigLoader.LoadFromProject(root, configDiagnostics);
        if (configDiagnostics.Any(d => d.Severity == DbDataBuild.Core.Severity.Error)) { diagnostics.AddRange(configDiagnostics.Where(d => d.Severity == DbDataBuild.Core.Severity.Error)); return null; }

        var rendered = Path.Combine(root, RenderCommand.RenderedDir);
        var connections = Directory.Exists(rendered)
            ? Directory.EnumerateDirectories(rendered).Select(Path.GetFileName).Where(n => n is not (null or "lowered")).Select(n => n!).Order(StringComparer.Ordinal).ToList() : [];
        string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        var plans = new List<PublishedPlan>();
        var tasks = new List<PackageTask>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var extra = new SortedSet<string>(StringComparer.Ordinal);       // files the loads name outside rendered/ (hook scripts)
        foreach (var connection in connections)
        {
            var planNames = RefreshViews.PlanNames(root, connection);
            if (planNames.Count == 0) continue;
            var loadsPath = Path.Combine(rendered, connection, LoadsDocument.FileName);
            if (!File.Exists(loadsPath)) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.RefreshPlanMissing, new(Relative(loadsPath), 0, 0), $"There is no `{Relative(loadsPath)}`, which the plans of `{connection}` run from.")); continue; }
            var parse = new List<Diagnostic>();
            if (LoadsDocument.Parse(File.ReadAllText(loadsPath), Relative(loadsPath), parse) is not { } loads) { diagnostics.AddRange(parse); continue; }
            if (loads.Connection != connection) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(Relative(loadsPath), 0, 0), $"The loads file is for `{loads.Connection}`, not `{connection}`.")); continue; }
            if (string.IsNullOrWhiteSpace(loads.ProjectHash)) diagnostics.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(Relative(loadsPath), 0, 0), "The loads file has no `project_hash`, which a refresh checks the deployed structure against."));

            // every script a load names is the one the file was compiled from (DDB-448), also those no plan runs: a load can be run on its own
            var changed = new List<Diagnostic>();
            foreach (var entry in loads.Loads)
                foreach (var (file, hash, what) in RefreshFiles.Named(entry))
                {
                    RefreshFiles.Read(root, file, hash, what, changed);
                    if (file.StartsWith("hooks/", StringComparison.Ordinal) || !file.Contains('/')) extra.Add(file);
                }
            diagnostics.AddRange(changed.DistinctBy(d => (d.Location.File, d.Found)));

            foreach (var plan in planNames)
            {
                var planPath = Path.Combine(rendered, RefreshPlanDocument.PathOf(connection, plan));
                var relative = Relative(planPath);
                var planDiagnostics = new List<Diagnostic>();
                if (RefreshPlanDocument.Parse(File.ReadAllText(planPath), relative, planDiagnostics) is not { } file) { diagnostics.AddRange(planDiagnostics); continue; }
                if (file.Connection != connection || file.Name != plan) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.PlanFileInvalid, new(relative, 0, 0), $"The refresh plan is for `{file.Name}` on `{file.Connection}`, not `{plan}` on `{connection}`.")); continue; }

                // the plan names each member's load plan by hash: one `loads.yml` does not have is a plan compiled before the loads were (DDB-448)
                foreach (var member in file.Members.Where(m => !loads.Loads.Any(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash)))
                    diagnostics.Add(new Diagnostic(DiagnosticCatalog.RefreshFileChanged, new(relative, 0, 0), $"The refresh plan names the load plan {member.LoadPlanHash[..Math.Min(12, member.LoadPlanHash.Length)]} of `{member.Model}.{member.Load}`, which `{Relative(loadsPath)}` does not have (it was compiled again since)."));

                var (schedule, problem) = RefreshJobs.ScheduleOf(file.Settings);
                if (problem is not null) { diagnostics.Add(Invalid(relative, $"`schedule`: {problem}", "Standard cron fields, for example `15 * * * *` (minute 15 of every hour) or `0 2 * * 1-5`; `time_zone` names the zone it is read in. Correct it in the model's `refresh:` plans and run `project compile`.")); continue; }

                var task = TaskName(connection, plan);
                if (!PackageManifestValidator.NameRule.IsMatch(task)) { diagnostics.Add(Invalid(relative, $"The plan `{connection}/{plan}` makes the task name `{task}`, which is not 1 to 64 lower-case letters, digits, `-` or `_`.")); continue; }
                if (named.TryGetValue(task, out var other)) { diagnostics.Add(Invalid(relative, $"The plans `{other}` and `{connection}/{plan}` both make the task `{task}`; rename one of them.")); continue; }
                named[task] = $"{connection}/{plan}";

                // the connections the plan needs: its own (read and write) and, for a copy, the origins it reads (their read login)
                var members = file.Members.Select(m => loads.Loads.FirstOrDefault(l => l.Model == m.Model && l.Load == m.Load && l.Hash == m.LoadPlanHash)).Where(l => l is not null).Select(l => l!).ToList();
                var slotNames = new List<string> { connection };
                slotNames.AddRange(members.Where(l => l.Copy is not null).SelectMany(l => l.Copy!.Origins.Select(o => o.Connection)).Distinct(StringComparer.Ordinal).Where(c => c != connection).Order(StringComparer.Ordinal));
                var slots = new List<ConnectionSlot>();
                foreach (var name in slotNames)
                {
                    if (config.EngineOf(name) is not { Length: > 0 } engine) { diagnostics.Add(Invalid(relative, $"The plan `{connection}/{plan}` uses the connection `{name}`, which the project has no `connections/{name}.yaml` for.")); continue; }
                    if (!PackageManifestValidator.IdentifierRule.IsMatch(name)) { diagnostics.Add(Invalid($"connections/{name}.yaml", $"The connection `{name}` is a slot of the package, and a slot's name is lower-case letters, digits and `_`, starting with a letter, at most 64.")); continue; }
                    slots.Add(new ConnectionSlot(name, engine, SlotEnvironmentFormats.BuildLogin));
                }

                var timeout = TimeoutOf(file, members);
                var limits = new TaskLimits(Timeout: timeout, Concurrency: 1);
                tasks.Add(new PackageTask(task, new TaskEntry(), schedule ?? new Schedule(), [],
                    [new TaskInput("connection", ProgressType.Text, Default: connection), new TaskInput("plan", ProgressType.Text, Default: plan)],
                    slots, ProgressDeclaration.Empty, limits, [], []));
                plans.Add(new PublishedPlan(connection, plan, task, relative, file.Settings.Schedule, file.Settings.Schedule is null ? null : file.Settings.TimeZone ?? "UTC", [.. slots.Select(s => s.Name)], timeout));
            }
        }

        if (tasks.Count == 0 && diagnostics.Count == before)
            diagnostics.Add(new Diagnostic(DiagnosticCatalog.RefreshPlanMissing, new(RenderCommand.RenderedDir, 0, 0), "The project has no compiled refresh plan to publish: no `rendered/<connection>/refresh/<plan>.yml`.", Fix: $"Declare plans with `refresh:` in a model, run `{ProductInfo.Cli} project compile` and commit `rendered/`."));
        if (diagnostics.Count > before) return null;

        var manifest = new PackageManifest(package, $"The dbdatabuild project {package}: {tasks.Count} refresh plan(s)",
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(RuntimeName, runtimeRange)),
            new BuiltAgainst($"{Environment.Version.Major}.{Environment.Version.Minor}", null, RunnerProtocol.V1), tasks);
        var text = PackageManifestWriter.Write(manifest);

        // what the runner reads: the manifest must come back from the reader as the same manifest (a changed format fails here, naming the file, and not on the runner)
        var load = PackageManifestReader.Load(text, "package.yaml");
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error)) diagnostics.Add(Invalid("package.yaml", d.ToString()));
            return null;
        }

        return new BuiltPackage(package, manifest, text, Files(root, rendered, extra), plans);
    }

    /// <summary>
    /// The time a plan may take: the sum of its loads' `max_runtime` (a load's own, else the plan's) and the plan's `max_wait`, plus a grace; null when a load has none, because then nothing bounds the run. The sum is an upper bound:
    /// loads that run in parallel finish earlier. `max_runtime` limits one load's statement; the task's timeout is what stops a run that has gone on beyond all of them.
    /// </summary>
    private static TimeSpan? TimeoutOf(RefreshPlanFile plan, IReadOnlyList<LoadPlanEntry> members)
    {
        if (members.Count == 0) return null;
        TimeSpan? Span(string? text) => text is not null && EveryDuration.TryParse(text) is { } d ? TimeSpan.FromSeconds(d.Seconds) : null;
        var planLimit = Span(plan.Settings.MaxRuntime);
        var total = TimeSpan.Zero;
        foreach (var load in members)
        {
            var limit = Span(load.MaxRuntime) ?? planLimit;
            if (limit is null) return null;
            total += limit.Value;
        }

        return total + (Span(plan.Settings.MaxWait) ?? TimeSpan.Zero) + TimeoutGrace;
    }

    private static List<PackageFile> Files(string root, string rendered, IEnumerable<string> extra)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Add(string relative, string full) { if (File.Exists(full)) files[relative] = full; }
        Add(ProductInfo.ConfigFile, Path.Combine(root, ProductInfo.ConfigFile));
        Add(ProductInfo.DialectLockFile, Path.Combine(root, ProductInfo.DialectLockFile));
        var connections = Path.Combine(root, "connections");
        if (Directory.Exists(connections))
            foreach (var path in Directory.EnumerateFiles(connections, "*", SearchOption.AllDirectories)) files[Path.GetRelativePath(root, path).Replace('\\', '/')] = path;
        if (Directory.Exists(rendered))
            foreach (var path in Directory.EnumerateFiles(rendered, "*", SearchOption.AllDirectories)) files[Path.GetRelativePath(root, path).Replace('\\', '/')] = path;
        foreach (var file in extra) Add(file, Path.GetFullPath(Path.Combine(root, file)));
        return [.. files.Select(f => new PackageFile(f.Key, f.Value))];
    }

    /// <summary>
    /// Writes the package into <paramref name="directory"/>: `package.yaml` and `files/&lt;path&gt;`. Text files are written with LF line endings whatever the checkout has (a rendered file is LF and is hashed as such), so one commit gives
    /// one package on every platform and publishing it again sends nothing.
    /// </summary>
    public static void Write(BuiltPackage package, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.yaml"), package.ManifestText, new UTF8Encoding(false));
        foreach (var file in package.Files)
        {
            var target = Path.Combine(directory, "files", file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = File.ReadAllBytes(file.Source);
            if (TextExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) && !bytes.Contains((byte)0) && bytes.Contains((byte)'\r'))
                bytes = new UTF8Encoding(false).GetBytes(new UTF8Encoding(false).GetString(bytes).Replace("\r\n", "\n"));
            File.WriteAllBytes(target, bytes);
        }
    }
}
