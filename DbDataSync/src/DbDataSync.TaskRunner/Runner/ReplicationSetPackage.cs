using System.Text;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Scheduling;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Lineage;
using DbDataKit.Sync.Core.Secrets;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataSync.Libraries;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>One file of the package: where it goes inside it and where it is now.</summary>
public sealed record SetPackageFile(string Path, string Source);

/// <summary>One replication as the task it becomes: its name on the runner, how it is scheduled there, the slots it needs and the Sync connection each slot is for (the connection of the runner it is bound to carries that name).</summary>
public sealed record PublishedReplication(string Replication, string Task, string Mode, string? Cron, int? FrequencySeconds, IReadOnlyList<string> Slots, int Mappings, IReadOnlyDictionary<string, string>? SlotConnections = null);

/// <summary>A replication set made into a `package/1` definition package: the manifest (text and model), the files of the definition and what each replication became.</summary>
/// <param name="Lineage">The design lineage the package carries (plan 18 G18, <see cref="DbDataKit.Lineage.PackageLineage"/>): the replications' tables and columns, by the identity of the repository's connections, as the drain reports what it wrote.</param>
public sealed record BuiltReplicationSet(string Name, PackageManifest Manifest, string ManifestText, IReadOnlyList<SetPackageFile> Files, IReadOnlyList<PublishedReplication> Replications, string? Lineage = null);

/// <summary>
/// Makes a Sync repository (the folder that holds <c>config/</c>, <c>libraries/</c>, <c>drivers/</c> and <c>files/</c>) into a definition package for a runner (plan 28c E08, section 4.3): one task per enabled replication on the
/// engine's runtime (`dbdatasync-runtime`), the definition under <c>repo/</c> in the package (the <c>repo_dir</c> a drain reads), the schedule from <see cref="SchedulingConfig"/>, a connection slot for every connection the replication reads or writes,
/// and the checkpoint of the drain declared as the manifest's progress. It compiles and contacts nothing: the problems a replication would meet at its first run are found here and named, so nothing is sent that cannot run.
/// <list type="bullet">
/// <item>a mapping whose source column cache is missing is refused with the text of <see cref="MetadataNotCachedException"/> (the engine reads the cache and nothing else, so a published mapping without one fails at its first pass);
/// one without a target cache is accepted: its table may not exist yet, the drain refuses until it does and the set's <c>provision</c> creates it (plan 28c E21, owner 2026-11-15);</item>
/// <item>a schedule that does not validate (<see cref="Schedule.Validate"/>, <see cref="ConfigValidation.ValidateScheduling"/>) is refused naming the replication and the key;</item>
/// <item>a replication or connection file that does not read is refused naming the file, by the strict readers the engine itself uses;</item>
/// <item><c>secrets/</c>, the git history, the state and the script cache never go in: the package holds no secret (a connection's login is a slot, bound on the runner).</item>
/// </list>
/// </summary>
public static class ReplicationSetPackage
{
    /// <summary>The runtime package that runs a Sync definition (plan 28c E06): <c>scripts/publish-runtime.sh</c>'s package name.</summary>
    public const string RuntimeName = "dbdatasync-runtime";

    /// <summary>
    /// How often a drain is run again when its process is killed or its host goes away (plan 28c E12g, owner decision 2026-11-12): the first run and two more. The retry chain of the runner restarts the same item with the same inputs after
    /// <see cref="DrainBackoff"/> (doubling up to <see cref="DrainMaxBackoff"/>), so a periodic drain, and the bulk load it carries as its <c>requests</c> input (a request applies once by its id), goes on at once and not at the next cron time.
    /// Only a temporary failure or a gone process is retried; a drain that ended with failed mappings, or a permanent failure, is not.
    /// </summary>
    public const int DrainAttempts = 3;

    /// <summary>The wait before the second run of a drain.</summary>
    public static readonly TimeSpan DrainBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The ceiling of the wait between runs of a drain.</summary>
    public static readonly TimeSpan DrainMaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>Where in the package the definition is: the <c>repo_dir</c> input of the drain.</summary>
    public const string RepoDir = DefinitionFiles.RepoDir;

    /// <summary>The name of the set's manual-only provisioning task (<see cref="ProvisionJob"/>, plan 28c E21); a replication may not take it.</summary>
    public const string ProvisionTask = DbDataSync.State.RunnerTaskNames.Provision;

    /// <summary>The name of the set's manual, read-only preflight (<see cref="SetPreflightJob"/>), which a control service runs before it serves a version in production; a replication may not take it.</summary>
    public const string PreflightTask = Preflight.TaskName;

    /// <summary>The names of the set's tasks a person waits for on a screen (<see cref="PreviewJob"/>, <see cref="TryStrategyJob"/>); a replication may not take them.</summary>
    public static readonly IReadOnlyList<string> PreviewTaskNames = [PreviewTasks.Preview, PreviewTasks.TryStrategy];

    /// <summary>The name of the set's manual task that tests a script against generated input (<see cref="ScriptTestJob"/>); it uses no connection, and a replication may not take it.</summary>
    public const string ScriptTestTask = ScriptTestJob.TaskName;
    /// <summary>The names of the set's tasks that read a connection's shape for a screen (<see cref="SetDiscoverJob"/>, <see cref="SetDescribeJob"/>); a replication may not take them.</summary>
    public static readonly IReadOnlyList<string> CatalogTaskNames = [SetCatalogSteps.Discover, SetCatalogSteps.Describe];

    /// <summary>Where the configuration repository keeps the files a message names (the layout of <c>ConfigPaths</c>, which is internal to the core).</summary>
    /// <summary>The .NET the package is built for: what this assembly targets (<c>net10.0</c> is "10.0"), not the runtime the Sync program happens to run on (it rolls forward to a newer major where one is installed).</summary>
    private static string TargetDotnet()
    {
        var framework = typeof(ReplicationSetPackage).Assembly.GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().FirstOrDefault()?.FrameworkName;
        var marker = "Version=v";
        var at = framework?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
        return at >= 0 && Version.TryParse(framework![(at + marker.Length)..], out var v) ? $"{v.Major}.{v.Minor}" : $"{Environment.Version.Major}.{Environment.Version.Minor}";
    }

    /// <summary>The C# the set compiles where it runs: its scripts (<c>config/scripts/*.csx</c>) and its compiled drivers (<c>drivers/&lt;id&gt;/driver.csx</c>).</summary>
    private static IEnumerable<string> CSharpFiles(string repoRoot, string configRoot)
    {
        var scripts = Path.Combine(configRoot, "scripts");
        var drivers = Path.Combine(repoRoot, "drivers");
        return (Directory.Exists(scripts) ? Directory.EnumerateFiles(scripts, "*.csx") : [])
            .Concat(Directory.Exists(drivers) ? Directory.EnumerateDirectories(drivers).Select(d => Path.Combine(d, "driver.csx")).Where(File.Exists) : [])
            .Order(StringComparer.Ordinal);
    }

    private static class Paths
    {
        public static string ReplicationsDir(string configRoot) => Path.Combine(configRoot, "replications");
        public static string TaskFile(string configRoot, string name) => Path.Combine(ReplicationsDir(configRoot), name, "task.yaml");
        public static string TableMappingFile(string configRoot, string name, string mapping) => Path.Combine(ReplicationsDir(configRoot), name, "table-mappings", mapping + ".yaml");
    }

    /// <summary>The range of runtimes a set published by this tool accepts: the release's own line, <c>2026.11.x</c>; <c>*</c> when the version is not numbers.</summary>
    public static string RuntimeRange(string version)
    {
        var numbers = version.Split('-', '+')[0].Split('.').Take(2).ToArray();
        return numbers.Length == 2 && numbers.All(n => n.Length > 0 && n.All(char.IsAsciiDigit)) ? $"{numbers[0]}.{numbers[1]}.x" : "*";
    }

    /// <summary>A name as the runner's name rule (lower-case letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit) writes it: what is not allowed becomes <c>-</c>.</summary>
    public static string NameOf(string text) => DbDataSync.State.RunnerTaskNames.Of(text);

    /// <summary>The slot a connection becomes: its name in the identifier alphabet (lower-case letters, digits and <c>_</c>, starting with a letter).</summary>
    public static string SlotNameOf(string connection) => ConnectionSlots.NameOf(connection);

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
            if (taskName == ProvisionTask) { problems.Add($"{file}: the replication `{task.Name}` makes the task `{taskName}`, the name of the set's provisioning task; rename the replication."); continue; }
            if (taskName == PreflightTask) { problems.Add($"{file}: the replication `{task.Name}` makes the task `{taskName}`, the name of the set's preflight; rename the replication."); continue; }
            if (taskName == ScriptTestTask) { problems.Add($"{file}: the replication `{task.Name}` makes the task `{taskName}`, the name of the set's test of a script; rename the replication."); continue; }
            if (CatalogTaskNames.Contains(taskName)) { problems.Add($"{file}: the replication `{task.Name}` makes the task `{taskName}`, the name of a task the set has for its screens (the read of a connection's tables and columns, the description of a query); rename the replication."); continue; }
            if (PreviewTaskNames.Contains(taskName)) { problems.Add($"{file}: the replication `{task.Name}` makes the task `{taskName}`, the name of a task the set has for its screens (the rows of a mapping, the trial of a segmenting strategy); rename the replication."); continue; }
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

                // only the source's cache: a target without one is a table that may not exist yet (plan 28c E21, owner 2026-11-15), which the set's `provision` creates when a person applies the setup;
                // until then its drain refuses, naming the table (TargetTableMissingException), and once the table is there the first pass reads its shape
                if (mapping.SourceColumns.Count == 0)
                    problems.Add($"{Paths.TableMappingFile(configRoot, task.Name, mappingName)}: {new MetadataNotCachedException(mapping.Name, "source").Message}");
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
            tasks.Add(drain with { Name = taskName, Entry = new TaskEntry(), Schedule = schedule, Inputs = inputs, Connections = slots, Limits = drain.Limits with { Attempts = DrainAttempts, Backoff = DrainBackoff, MaxBackoff = DrainMaxBackoff } });   // no `concurrency`: it limits the runs of the whole package, which would serialize the replications of the set (and a continuous drain, which stays up, would starve the others); the queue already keeps one task from running twice
            published.Add(new PublishedReplication(task.Name, taskName, task.Scheduling.Mode.ToString(), task.Scheduling.CronExpression, task.Scheduling.FrequencySeconds, [.. slots.Select(s => s.Name)], mappings.Count, new Dictionary<string, string>(slotNames, StringComparer.Ordinal)));
        }

        // a script or a driver.csx that names the namespaces Sync's libraries had before: it would not compile where the set runs
        foreach (var code in CSharpFiles(repoRoot, configRoot))
            foreach (var old in RenamedNamespaces.Find(File.ReadAllText(code)))
                problems.Add($"{code}: {old}");

        if (tasks.Count == 0 && problems.Count == before)
            problems.Add($"{Paths.ReplicationsDir(configRoot)}: the repository has no enabled replication to publish.");
        if (problems.Count > before) return null;

        // the set's database setup (plan 28c E21): one manual-only task over every connection of the set, run by hand under the name of the person who asked, never on a schedule, never retried
        var allSlots = tasks.SelectMany(t => t.Connections).GroupBy(s => s.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        var provision = ManifestGenerator.TaskOf(typeof(ProvisionJob));
        tasks.Add(provision with
        {
            Entry = new TaskEntry(), Schedule = new Schedule(), Triggers = [TaskTrigger.ByHand()], Connections = allSlots,
            Inputs = [.. provision.Inputs.Select(i => i.Name == "repo_dir" ? i with { Default = RepoDir } : i)],
            Limits = provision.Limits with { Attempts = 1 },
        });

        // the set's preflight (Design: docs/authoring-metadata.md#a-sync-project): read only, by hand, over the same connections, which a control service runs on a version staged in production before it serves it
        tasks.Add(Preflight.Declare(allSlots, inputs: SetPreflightSteps.Inputs(RepoDir)));

        // what a person waits for on a screen (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept): read only, by hand, over the same connections, answered to the console and kept
        // nowhere; two people may preview at once, and a failed one is asked again, never retried
        tasks.Add(Screen(typeof(PreviewJob), allSlots, PreviewTasks.PreviewInputs(RepoDir)));
        tasks.Add(Screen(typeof(TryStrategyJob), allSlots, PreviewTasks.TryStrategyInputs(RepoDir)));

        // the test of a script of the version against generated input (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner): by hand, answered and kept nowhere, as the two above, and over no connection
        tasks.Add(Screen(typeof(ScriptTestJob), [], ScriptTestJob.Inputs(RepoDir)));
        // the shape of a connection whose driver is Sync's own, for the same screens (Design: docs/authoring-metadata.md#a-sync-project): read only, by hand, over the same connections, answered in a step keyed by the run as the
        // runner's built-in package answers
        tasks.Add(Screen(typeof(SetDiscoverJob), allSlots, SetCatalogSteps.DiscoverInputs(RepoDir)));
        tasks.Add(Screen(typeof(SetDescribeJob), allSlots, SetCatalogSteps.DescribeInputs(RepoDir)));

        var manifest = new PackageManifest(package, $"The Sync replication set {package}: {tasks.Count} replication(s)",
            new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference(RuntimeName, runtimeRange)),
            new BuiltAgainst(TargetDotnet(), null, RunnerProtocol.V1), tasks);
        var text = PackageManifestWriter.Write(manifest);

        // what the runner reads: the manifest must come back from the reader as the same manifest, and pass the validator (a changed format fails here, naming the file, and not on the runner)
        var load = PackageManifestReader.Load(text, "package.yaml");
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error)) problems.Add($"package.yaml: {d}");
            return null;
        }

        // the replications' design lineage (plan 18 G18): what the deleted Sync server's lineage served, carried by the package for the Runner's; a replication that does not read is already a problem above
        var lineageProblems = new List<string>();
        var (replications, lineageConnections) = SyncLineage.Load(config, lineageProblems);
        var lineage = DbDataKit.Lineage.PackageLineage.Write(SyncLineage.Design([.. replications.Where(r => r.Task.Enabled)], lineageConnections));

        return new BuiltReplicationSet(package, manifest, text, Files(repoRoot), published, lineage);
    }

    /// <summary>A task a person starts from a screen and waits for: by hand only, one attempt, five minutes at most, overlapping itself.</summary>
    private static PackageTask Screen(Type job, IReadOnlyList<ConnectionSlot> slots, IReadOnlyList<TaskInput> inputs)
    {
        var declared = ManifestGenerator.TaskOf(job);
        return declared with
        {
            Entry = new TaskEntry(), Schedule = new Schedule(), Triggers = [TaskTrigger.ByHand()], Connections = slots, Inputs = inputs, AllowOverlap = true,
            Limits = declared.Limits with { Attempts = 1, Timeout = TimeSpan.FromMinutes(5) },
        };
    }

    /// <summary>The schedule a replication has on the runner: a periodic one its cron expression (UTC, as the Sync scheduler reads it: its drain plans once, runs what is queued and ends), a continuous one its frequency as the schedule that starts the drain again when it has ended (a continuous drain stays up and plans between passes itself, with the planner of the API, until its idle timeout; plan 28c E11, E12). Who schedules the replication is its owner (`old`, `observe`, `runner`), which a publish does not change.</summary>
    public static Schedule ScheduleOf(SchedulingConfig scheduling, string replication) => scheduling.Mode switch
    {
        ScheduleMode.Periodic when string.IsNullOrWhiteSpace(scheduling.CronExpression) => throw new InvalidOperationException($"Replication '{replication}' is periodic and has no cron expression."),
        ScheduleMode.Periodic => Schedule.OnCron(scheduling.CronExpression!),
        _ => Schedule.Interval(scheduling.Frequency),
    };

    /// <summary>The definition's files under <c>repo/</c> (the list <see cref="DefinitionFiles"/> keeps, which the check for unpublished changes reads too).</summary>
    private static List<SetPackageFile> Files(string repoRoot) => [.. DefinitionFiles.Enumerate(repoRoot).Select(f => new SetPackageFile(f.Path, f.Source))];

    /// <summary>
    /// Adds the <c>driver-libraries</c> part for <paramref name="platform"/> (<see cref="DriverLibrariesPart"/>, built into <paramref name="partDirectory"/>, which has to be empty) to the package's files, under <c>repo/</c>, and returns the set with them.
    /// </summary>
    public static async Task<BuiltReplicationSet> AddLibrariesAsync(BuiltReplicationSet built, string repoRoot, string partDirectory, string platform, string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        // the built-in drivers' libraries the set's connections need travel too, when the repository does not hold them (the deleted Sync server seeded them into it at a connection's first open; plan 33 SC23d2)
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), SecretStore.ForProviders());
        var driverTypes = new List<string>();
        foreach (var name in config.ListConnections())
        {
            try { driverTypes.Add(config.LoadConnection(name).DriverType); }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ConfigValidationException or FileNotFoundException) { /* the check names a connection it cannot read */ }
        }
        await DriverLibrariesPart.BuildAsync(repoRoot, partDirectory, platform, DbDataSync.State.BuiltInDriverLibraries.CatalogManifestsFor(driverTypes), nugetSource, cancellationToken);
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
                var current = DbDataKit.Sync.Core.Config.PublishedConfigText.Current(file.Path[(RepoDir.Length + 1)..], text);
                if (current != text) bytes = new UTF8Encoding(false).GetBytes(current);
            }
            File.WriteAllBytes(target, bytes);
        }

        if (package.Lineage is { } lineage)
        {
            var target = Path.Combine(directory, "files", DbDataKit.Lineage.PackageLineage.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, DbDataKit.Lineage.PackageLineage.Bytes(lineage));
        }
    }
}
