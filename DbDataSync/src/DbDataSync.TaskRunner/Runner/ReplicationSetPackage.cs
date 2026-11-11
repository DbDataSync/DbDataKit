using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Scheduling;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>One file of the package: where it goes inside it and where it is now.</summary>
public sealed record SetPackageFile(string Path, string Source);

/// <summary>One replication as the task it becomes: its name on the runner, how it is scheduled there, the slots it needs and the Sync connection each slot is for (the connection of the runner it is bound to carries that name).</summary>
public sealed record PublishedReplication(string Replication, string Task, string Mode, string? Cron, int? FrequencySeconds, IReadOnlyList<string> Slots, int Mappings, IReadOnlyDictionary<string, string>? SlotConnections = null);

/// <summary>A replication set made into a `package/1` definition package: the manifest (text and model), the files of the definition and what each replication became.</summary>
public sealed record BuiltReplicationSet(string Name, PackageManifest Manifest, string ManifestText, IReadOnlyList<SetPackageFile> Files, IReadOnlyList<PublishedReplication> Replications);

/// <summary>
/// Makes a Sync repository (the folder that holds <c>config/</c>, <c>libraries/</c>, <c>drivers/</c> and <c>files/</c>) into a definition package for a runner (plan 28c E08, section 4.3): one task per enabled replication on the
/// engine's runtime (`dbdatasync-runtime`), the definition under <c>repo/</c> in the package (the <c>repo_dir</c> a drain reads), the schedule from <see cref="SchedulingConfig"/>, a connection slot for every connection the replication reads or writes,
/// and the checkpoint of the drain declared as the manifest's progress. It compiles and contacts nothing: the problems a replication would meet at its first run are found here and named, so nothing is sent that cannot run.
/// <list type="bullet">
/// <item>a mapping whose column cache is missing is refused with the text of <see cref="MetadataNotCachedException"/> (the engine reads the cache and nothing else, so a published mapping without one fails at its first pass);</item>
/// <item>a schedule that does not validate (<see cref="Schedule.Validate"/>, <see cref="ConfigValidation.ValidateScheduling"/>) is refused naming the replication and the key;</item>
/// <item>a replication or connection file that does not read is refused naming the file, by the strict readers the engine itself uses;</item>
/// <item><c>secrets/</c>, the git history, the state and the script cache never go in: the package holds no secret (a connection's login is a slot, bound on the runner).</item>
/// </list>
/// </summary>
public static class ReplicationSetPackage
{
    /// <summary>The runtime package that runs a Sync definition (plan 28c E06): <c>scripts/publish-runtime.sh</c>'s package name.</summary>
    public const string RuntimeName = "dbdatasync-runtime";

    /// <summary>Where in the package the definition is: the <c>repo_dir</c> input of the drain.</summary>
    public const string RepoDir = "repo";

    /// <summary>Where the configuration repository keeps the files a message names (the layout of <c>ConfigPaths</c>, which is internal to the core).</summary>
    private static class Paths
    {
        public static string ReplicationsDir(string configRoot) => Path.Combine(configRoot, "replications");
        public static string TaskFile(string configRoot, string name) => Path.Combine(ReplicationsDir(configRoot), name, "task.yaml");
        public static string TableMappingFile(string configRoot, string name, string mapping) => Path.Combine(ReplicationsDir(configRoot), name, "table-mappings", mapping + ".yaml");
    }

    private static readonly string[] SkippedDirectories = [".git", "secrets", "state", "script-cache", "node_modules"];

    /// <summary>The range of runtimes a set published by this tool accepts: the release's own line, <c>2026.11.x</c>; <c>*</c> when the version is not numbers.</summary>
    public static string RuntimeRange(string version)
    {
        var numbers = version.Split('-', '+')[0].Split('.').Take(2).ToArray();
        return numbers.Length == 2 && numbers.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)) ? $"{numbers[0]}.{numbers[1]}.x" : "*";
    }

    /// <summary>A name as the runner's name rule (lower-case letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit) writes it: what is not allowed becomes <c>-</c>.</summary>
    public static string NameOf(string text) => DbDataSync.State.RunnerTaskNames.Of(text);

    /// <summary>The slot a connection becomes: its name in the identifier alphabet (lower-case letters, digits and <c>_</c>, starting with a letter).</summary>
    public static string SlotNameOf(string connection)
    {
        var name = Regex.Replace(connection.ToLowerInvariant(), "[^a-z0-9_]", "_");
        if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "c_" + name;
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>
    /// The package of the repository in <paramref name="repoRoot"/>, or null with the reasons in <paramref name="problems"/>. <paramref name="package"/> is the package name, <paramref name="runtimeRange"/> the engines the set accepts.
    /// </summary>
    public static BuiltReplicationSet? Build(string repoRoot, string package, string runtimeRange, List<string> problems)
    {
        var before = problems.Count;
        if (!PackageManifestValidator.NameRule.IsMatch(package))
        {
            problems.Add($"--package: `{package}` is not a package name: 1 to 64 lower-case letters, digits, `-` or `_`, starting with a letter or digit.");
            return null;
        }

        var configRoot = Path.Combine(repoRoot, "config");
        if (!Directory.Exists(configRoot))
        {
            problems.Add($"{configRoot}: there is no `config/` folder: this is not a Sync repository.");
            return null;
        }

        var config = ConfigRepository.ReadOnly(configRoot, SecretStore.ForProviders());
        var drain = ManifestGenerator.TaskOf(typeof(DrainJob));
        var tasks = new List<PackageTask>();
        var published = new List<PublishedReplication>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var connections = new Dictionary<string, ConnectionConfig>(StringComparer.Ordinal);
        foreach (var name in config.ListReplications())
        {
            var file = Paths.TaskFile(configRoot, name);
            ReplicationTaskConfig task;
            try { task = config.LoadReplicationTask(name); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException or ConfigValidationException)
            {
                problems.Add($"{file}: {ex.Message}");
                continue;
            }

            if (!task.Enabled) continue;       // a replication that is switched off is not published: it would only be a task that never runs
            var taskName = NameOf(task.Name);
            if (!PackageManifestValidator.NameRule.IsMatch(taskName)) { problems.Add($"{file}: the replication `{task.Name}` makes the task name `{taskName}`, which is not 1 to 64 lower-case letters, digits, `-` or `_`."); continue; }
            if (named.TryGetValue(taskName, out var other)) { problems.Add($"{file}: the replications `{other}` and `{task.Name}` both make the task `{taskName}`; rename one of them."); continue; }
            named[taskName] = task.Name;

            Schedule schedule;
            try
            {
                ConfigValidation.ValidateScheduling(task.Scheduling, task.Name);
                schedule = ScheduleOf(task.Scheduling, task.Name);
            }
            catch (Exception ex) when (ex is ConfigValidationException or InvalidOperationException)
            {
                problems.Add($"{file}: `scheduling`: {ex.Message}");
                continue;
            }

            if (schedule.Validate() is { } why) { problems.Add($"{file}: `scheduling.cronExpression`: {why}"); continue; }

            // the connections the replication reads or writes, and the cache the engine reads and nothing else (a mapping without one fails at its first pass)
            var used = new SortedSet<string>(StringComparer.Ordinal);
            var mappings = config.ListTableMappings(task.Name);
            foreach (var mappingName in mappings)
            {
                TableMappingConfig mapping;
                try { mapping = config.LoadTableMapping(task.Name, mappingName); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException or MappingLoadingException)
                {
                    problems.Add($"{Paths.TableMappingFile(configRoot, task.Name, mappingName)}: {ex.Message}");
                    continue;
                }

                try
                {
                    foreach (var source in mapping.Sources) used.Add(EndpointResolution.ResolveSource(task, source).ConnectionName);
                    foreach (var target in mapping.Targets) used.Add(EndpointResolution.ResolveTarget(task, target).ConnectionName);
                }
                catch (InvalidOperationException ex) { problems.Add($"{Paths.TableMappingFile(configRoot, task.Name, mappingName)}: {ex.Message}"); continue; }

                if (mapping.SourceColumns.Count == 0)
                    problems.Add($"{Paths.TableMappingFile(configRoot, task.Name, mappingName)}: {new MetadataNotCachedException(mapping.Name, "source").Message}");
                else if (mapping.TargetColumns.Count == 0 && !ProvisioningResolution.CreateTargetTableIfMissing(task, mapping))
                    problems.Add($"{Paths.TableMappingFile(configRoot, task.Name, mappingName)}: {new MetadataNotCachedException(mapping.Name, "target").Message}");
            }

            var slots = new List<ConnectionSlot>();
            var slotNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var connectionName in used)
            {
                if (!connections.TryGetValue(connectionName, out var connection))
                {
                    try { connection = connections[connectionName] = config.LoadConnection(connectionName); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or YamlDotNet.Core.YamlException or ConfigValidationException)
                    {
                        problems.Add($"{file}: the replication uses the connection `{connectionName}`, which does not read: {ex.Message}");
                        continue;
                    }
                }

                var slot = SlotNameOf(connectionName);
                if (slotNames.TryGetValue(slot, out var clash)) { problems.Add($"{file}: the connections `{clash}` and `{connectionName}` both make the slot `{slot}`; rename one of them."); continue; }
                slotNames[slot] = connectionName;
                slots.Add(new ConnectionSlot(slot, DriverFailures.EngineFor(connection.DriverType).Value, SlotEnvironmentFormats.SyncSecret));
            }

            // the drain, named for the replication: its inputs say which replication of the definition it drains and where the definition is in the package
            var inputs = drain.Inputs.Select(i => i.Name switch
            {
                "replication" => i with { Default = task.Name },
                "repo_dir" => i with { Default = RepoDir },
                _ => i,
            }).ToList();
            tasks.Add(drain with { Name = taskName, Entry = new TaskEntry(), Schedule = schedule, Inputs = inputs, Connections = slots });   // no `concurrency`: it limits the runs of the whole package, which would serialize the replications of the set (and a continuous drain, which stays up, would starve the others); the queue already keeps one task from running twice
            published.Add(new PublishedReplication(task.Name, taskName, task.Scheduling.Mode.ToString(), task.Scheduling.CronExpression, task.Scheduling.FrequencySeconds, [.. slots.Select(s => s.Name)], mappings.Count, new Dictionary<string, string>(slotNames, StringComparer.Ordinal)));
        }

        if (tasks.Count == 0 && problems.Count == before)
            problems.Add($"{Paths.ReplicationsDir(configRoot)}: the repository has no enabled replication to publish.");
        if (problems.Count > before) return null;

        var manifest = new PackageManifest(package, $"The Sync replication set {package}: {tasks.Count} replication(s)",
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(RuntimeName, runtimeRange)),
            new BuiltAgainst($"{Environment.Version.Major}.{Environment.Version.Minor}", null, RunnerProtocol.V1), tasks);
        var text = PackageManifestWriter.Write(manifest);

        // what the runner reads: the manifest must come back from the reader as the same manifest, and pass the validator (a changed format fails here, naming the file, and not on the runner)
        var load = PackageManifestReader.Load(text, "package.yaml");
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error)) problems.Add($"package.yaml: {d}");
            return null;
        }

        return new BuiltReplicationSet(package, manifest, text, Files(repoRoot), published);
    }

    /// <summary>The schedule a replication has on the runner: a periodic one its cron expression (UTC, as the Sync scheduler reads it: its drain plans once, runs what is queued and ends), a continuous one its frequency as the schedule that starts the drain again when it has ended (a continuous drain stays up and plans between passes itself, with the planner of the API, until its idle timeout; plan 28c E11, E12). Who schedules the replication is its owner (`old`, `observe`, `runner`), which a publish does not change.</summary>
    public static Schedule ScheduleOf(SchedulingConfig scheduling, string replication) => scheduling.Mode switch
    {
        ScheduleMode.Periodic when string.IsNullOrWhiteSpace(scheduling.CronExpression) => throw new InvalidOperationException($"Replication '{replication}' is periodic and has no cron expression."),
        ScheduleMode.Periodic => Schedule.OnCron(scheduling.CronExpression!),
        _ => Schedule.Interval(scheduling.Frequency),
    };

    /// <summary>
    /// The definition's files under <c>repo/</c>: everything of <c>config/</c>, <c>libraries/</c> (their scripts and pins; the stores come with <see cref="AddLibrariesAsync"/>), <c>drivers/</c> and <c>files/</c> except what is
    /// local to a machine (<c>secrets/</c>, <c>.git</c>, the state, the script cache) and a library's or driver's own <c>lib/</c>, which is rebuilt for the platform.
    /// </summary>
    private static List<SetPackageFile> Files(string repoRoot)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var top in new[] { "config", "libraries", "drivers", "files" })
        {
            var root = Path.Combine(repoRoot, top);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Take(parts.Length - 1).Any(p => SkippedDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
                if (top is "libraries" or "drivers" && parts.Length > 3 && parts[2] == LibraryPaths.LibDirName) continue;
                files[RepoDir + "/" + relative] = path;
            }
        }

        return [.. files.Select(f => new SetPackageFile(f.Key, f.Value))];
    }

    /// <summary>
    /// Adds the <c>driver-libraries</c> part for <paramref name="platform"/> (<see cref="DriverLibrariesPart"/>, built into <paramref name="partDirectory"/>, which has to be empty) to the package's files, under <c>repo/</c>, and returns the set with them.
    /// </summary>
    public static async Task<BuiltReplicationSet> AddLibrariesAsync(BuiltReplicationSet built, string repoRoot, string partDirectory, string platform, string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        await DriverLibrariesPart.BuildAsync(repoRoot, partDirectory, platform, nugetSource, cancellationToken);
        var files = built.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(partDirectory, "*", SearchOption.AllDirectories))
            files[RepoDir + "/" + Path.GetRelativePath(partDirectory, path).Replace('\\', '/')] = new SetPackageFile(RepoDir + "/" + Path.GetRelativePath(partDirectory, path).Replace('\\', '/'), path);
        return built with { Files = [.. files.Values.OrderBy(f => f.Path, StringComparer.Ordinal)] };
    }

    /// <summary>Writes the package into <paramref name="directory"/>: <c>package.yaml</c> and <c>files/&lt;path&gt;</c>, text files with LF line endings whatever the checkout has (so one commit gives one package on every platform and publishing it again sends nothing).</summary>
    public static void Write(BuiltReplicationSet package, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package.yaml"), package.ManifestText, new UTF8Encoding(false));
        var textExtensions = new[] { ".yml", ".yaml", ".sql", ".csx", ".json", ".md", ".txt", ".lock" };
        foreach (var file in package.Files)
        {
            var target = Path.Combine(directory, "files", file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var bytes = File.ReadAllBytes(file.Source);
            if (textExtensions.Contains(Path.GetExtension(file.Path), StringComparer.OrdinalIgnoreCase) && !bytes.Contains((byte)0) && bytes.Contains((byte)'\r'))
                bytes = new UTF8Encoding(false).GetBytes(new UTF8Encoding(false).GetString(bytes).Replace("\r\n", "\n"));
            // a configuration file of a versioned kind leaves in the current format, whatever its own number says (plan 33 6d, rule 5); the file in the repository is left as written
            if (file.Path.StartsWith(RepoDir + "/", StringComparison.Ordinal) && Path.GetExtension(file.Path) == ".yaml" && !bytes.Contains((byte)0))
            {
                var text = new UTF8Encoding(false).GetString(bytes);
                var current = DbDataSync.Core.Config.PublishedConfigText.Current(file.Path[(RepoDir.Length + 1)..], text);
                if (current != text) bytes = new UTF8Encoding(false).GetBytes(current);
            }
            File.WriteAllBytes(target, bytes);
        }
    }
}
