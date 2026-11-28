using System.Security.Cryptography;
using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataSync.TaskRunner.Runner;
using Xunit;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>Plan 28c E08: a Sync repository made into a definition package: one task per enabled replication, the schedule, the slots, the checkpoint, the files, and the problems found before anything is sent.</summary>
public sealed class ReplicationSetPackageTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-e08-").FullName;
    private readonly ConfigRepository _config;

    public ReplicationSetPackageTests()
    {
        _config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _config.SaveConnection(new ConnectionInput { Name = "crm-src", DriverType = DriverIds.MsSql, Host = "src", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "super-secret-pw" }, Author);
        _config.SaveConnection(new ConnectionInput { Name = "dwh", DriverType = DriverIds.Postgres, Host = "dw", Database = "dw", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "other-secret-pw" }, Author);
        Replication("orders", ScheduleMode.Periodic, cron: "*/15 * * * *");
        Replication("customers", ScheduleMode.Periodic, cron: "0 2 * * *");
        Replication("events", ScheduleMode.Continuous, frequency: 30);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private void Replication(string name, ScheduleMode mode, string? cron = null, int? frequency = null, bool cached = true, bool enabled = true, bool targetCached = true)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Enabled = enabled,
            Scheduling = new SchedulingConfig { Mode = mode, CronExpression = cron, FrequencySeconds = frequency, IdleTimeoutSeconds = frequency is null ? null : frequency * 4 },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "PostgresMerge" },
            },
        }, Author);
        for (var i = 1; i <= 2; i++)
            _config.SaveTableMapping(name, new TableMappingConfig
            {
                Name = $"t{i}",
                Sources = [new SourceTableSpec { ConnectionName = "crm-src", Database = "App", Schema = "dbo", Table = $"T{i}" }],
                Targets = [new TableSpec { ConnectionName = "dwh", Database = "dw", Schema = "public", Table = $"t{i}" }],
                SourceColumns = cached ? [new CachedColumn("id", "bigint", false, true, false, 1)] : [],
                TargetColumns = cached && targetCached ? [new CachedColumn("id", "bigint", false, true, false, 1)] : [],
            }, Author);
    }

    private string Library()
    {
        var dir = Path.Combine(_root, "libraries", "npgsql");
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "library.csx"), "#r \"nuget: Npgsql, 9.0.3\"\n");
        File.WriteAllText(Path.Combine(dir, "library.lock"), "{}\n");
        File.WriteAllText(Path.Combine(dir, "lib", "Npgsql.dll"), "not part of the package");
        return dir;
    }

    private BuiltReplicationSet Build(out List<string> problems, string package = "sales")
    {
        problems = [];
        return ReplicationSetPackage.Build(_root, package, "2026.11.x", problems)!;
    }

    [Fact]
    public void A_script_or_driver_with_a_using_of_an_old_namespace_is_refused_naming_the_new_one()
    {
        var scripts = Path.Combine(_root, "config", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "upper.csx"), "using System;\nusing DbDataSync.Scripting.Abstractions;\n\nnew Upper()\n");
        var driver = Path.Combine(_root, "drivers", "acme");
        Directory.CreateDirectory(driver);
        File.WriteAllText(Path.Combine(driver, "driver.csx"), "using DbDataSync.Drivers.Abstractions;\nnew AcmeDriver()\n");

        var problems = new List<string>();
        Assert.Null(ReplicationSetPackage.Build(_root, "sales", "2026.11.x", problems));

        Assert.Contains(problems, p => p.EndsWith("upper.csx: line 2: `using DbDataSync.Scripting.Abstractions;` names a namespace that is now `DbDataKit.Sync.Scripting.Abstractions`; change the using.", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("driver.csx: line 1:", StringComparison.Ordinal) && p.Contains("`DbDataKit.Sync.Drivers.Abstractions`", StringComparison.Ordinal));
    }

    [Fact]
    public void ThreeReplicationsMakeThreeTasksWithTheirScheduleSlotsAndInputs_AndTheManifestRoundTrips()
    {
        var built = Build(out var problems);

        Assert.Empty(problems);
        Assert.Equal(["customers", "events", "orders", ReplicationSetPackage.ProvisionTask, ReplicationSetPackage.PreflightTask, PreviewTasks.Preview, PreviewTasks.TryStrategy, ReplicationSetPackage.ScriptTestTask, SetCatalogSteps.Discover, SetCatalogSteps.Describe], built.Manifest.Tasks.Select(t => t.Name));
        var drains = built.Manifest.Tasks.Where(t => t.Name is not (ReplicationSetPackage.ProvisionTask or ReplicationSetPackage.PreflightTask or PreviewTasks.Preview or PreviewTasks.TryStrategy or SetCatalogSteps.Discover or SetCatalogSteps.Describe or ReplicationSetPackage.ScriptTestTask)).ToList();
        var orders = built.Manifest.Tasks.Single(t => t.Name == "orders");
        Assert.Equal("*/15 * * * *", orders.Schedule.Cron);
        Assert.Equal(TimeSpan.FromSeconds(30), built.Manifest.Tasks.Single(t => t.Name == "events").Schedule.Every);
        Assert.Equal(["crm_src", "dwh"], orders.Connections.Select(c => c.Name));
        Assert.Equal(["sqlserver", "postgres"], orders.Connections.Select(c => c.Kind));     // the engine ids the runner's connections have
        Assert.All(orders.Connections, c => Assert.Equal(SlotEnvironmentFormats.SyncSecret, c.Env));
        Assert.Equal("orders", orders.Inputs.Single(i => i.Name == "replication").Default);
        Assert.Equal(ReplicationSetPackage.RepoDir, orders.Inputs.Single(i => i.Name == "repo_dir").Default);
        // plan 28c E12g: a killed drain is run again by the retry chain, so a periodic bulk load does not wait for its next cron time; it survives the manifest's text
        Assert.All(drains, t => Assert.Equal((ReplicationSetPackage.DrainAttempts, ReplicationSetPackage.DrainBackoff, ReplicationSetPackage.DrainMaxBackoff), (t.Limits.Attempts, t.Limits.Backoff, t.Limits.MaxBackoff)));
        Assert.Contains("attempts: 3", built.ManifestText);
        Assert.Null(orders.Limits.Concurrency);   // plan 28c E12e: a package-wide limit would serialize the replications of the set, and a continuous drain would starve the others
        Assert.Equal((ReplicationSetPackage.RuntimeName, "2026.11.x"), (built.Manifest.Launch.Runtime!.Name, built.Manifest.Launch.Runtime.Range));

        // the checkpoint of the drain is declared, once per task, as the engine keeps it
        Assert.Contains("watermark", built.ManifestText);
        Assert.All(drains, t => Assert.NotEqual(DbDataKit.State.ProgressDeclaration.Empty, t.Progress));

        // plan 28c E21: the set's database setup is one manual-only task over every connection of the set, never scheduled, never retried
        var provision = built.Manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.ProvisionTask);
        Assert.Null(provision.Schedule.Cron);
        Assert.Null(provision.Schedule.Every);
        Assert.Equal(1, provision.Limits.Attempts);
        Assert.Equal(drains.SelectMany(t => t.Connections).Select(c => c.Name).Distinct().Order(StringComparer.Ordinal), provision.Connections.Select(c => c.Name));
        Assert.Equal(["replication", "mapping", "action", "steps", "repo_dir"], provision.Inputs.Select(i => i.Name));
        Assert.Equal(ReplicationSetPackage.RepoDir, provision.Inputs.Single(i => i.Name == "repo_dir").Default);

        // the set's preflight: read only, by hand, over the same connections, one run at a time, one attempt, every input with a default (a control service starts it with none),
        // and the step whose documents the control service reads before it serves a version in prod
        var preflight = built.Manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.PreflightTask);
        Assert.Equal(DbDataKit.Runner.Preflight.TaskName, preflight.Name);
        Assert.Null(preflight.Schedule.Cron);
        Assert.Null(preflight.Schedule.Every);
        Assert.Equal((1, 1), (preflight.Limits.Attempts, preflight.Limits.Concurrency));
        Assert.Equal(provision.Connections.Select(c => c.Name), preflight.Connections.Select(c => c.Name));
        Assert.Equal([("replication", ""), ("repo_dir", ReplicationSetPackage.RepoDir)], preflight.Inputs.Select(i => (i.Name, i.Default)));
        Assert.Equal([DbDataKit.Runner.Preflight.StepName], preflight.StepList.Select(s => s.Name));

        // what a person waits for on a screen: by hand only, one attempt, overlapping itself (two people preview at once), over the same connections, recording no step (the answer is kept nowhere)
        foreach (var name in new[] { PreviewTasks.Preview, PreviewTasks.TryStrategy })
        {
            var screen = built.Manifest.Tasks.Single(t => t.Name == name);
            Assert.Null(screen.Schedule.Cron);
            Assert.Null(screen.Schedule.Every);
            Assert.Equal((1, true), (screen.Limits.Attempts, screen.AllowOverlap));
            Assert.Equal(provision.Connections.Select(c => c.Name), screen.Connections.Select(c => c.Name));
            Assert.Empty(screen.StepList);
            Assert.All(screen.Inputs, i => Assert.NotNull(i.Default));
        }

        // the shape of a connection whose driver is Sync's own, for the same screens: by hand only, one attempt, overlapping itself, over the same connections, each keeping its answer in a step named after it
        foreach (var name in ReplicationSetPackage.CatalogTaskNames)
        {
            var read = built.Manifest.Tasks.Single(t => t.Name == name);
            Assert.Null(read.Schedule.Cron);
            Assert.Null(read.Schedule.Every);
            Assert.Equal((1, true), (read.Limits.Attempts, read.AllowOverlap));
            Assert.Equal(provision.Connections.Select(c => c.Name), read.Connections.Select(c => c.Name));
            Assert.Equal([name], read.StepList.Select(s => s.Name));
            Assert.All(read.Inputs, i => Assert.NotNull(i.Default));
        }

        Assert.Equal(["connection", "schemas", "pattern", "table", "repo_dir"], built.Manifest.Tasks.Single(t => t.Name == SetCatalogSteps.Discover).Inputs.Select(i => i.Name));
        Assert.Equal(["connection", "sql", "repo_dir"], built.Manifest.Tasks.Single(t => t.Name == SetCatalogSteps.Describe).Inputs.Select(i => i.Name));
        Assert.Equal(["replication", "mapping", "rows", "repo_dir", "mapping_text"], built.Manifest.Tasks.Single(t => t.Name == PreviewTasks.Preview).Inputs.Select(i => i.Name));
        Assert.Equal(["replication", "mapping", "strategy", "name", "column", "repo_dir", "mapping_text"], built.Manifest.Tasks.Single(t => t.Name == PreviewTasks.TryStrategy).Inputs.Select(i => i.Name));

        // the test of a script: by hand, one attempt, overlapping itself, recording no step, and over no connection (it compiles and runs the script against generated input)
        var scriptTest = built.Manifest.Tasks.Single(t => t.Name == ReplicationSetPackage.ScriptTestTask);
        Assert.Null(scriptTest.Schedule.Cron);
        Assert.Null(scriptTest.Schedule.Every);
        Assert.Equal((1, true), (scriptTest.Limits.Attempts, scriptTest.AllowOverlap));
        Assert.Empty(scriptTest.Connections);
        Assert.Empty(scriptTest.StepList);
        Assert.Equal([("name", ""), ("rows", "5"), ("repo_dir", ReplicationSetPackage.RepoDir)], scriptTest.Inputs.Select(i => (i.Name, i.Default)));

        var load = PackageManifestReader.Load(built.ManifestText, "package.yaml");
        Assert.True(load.Ok, string.Join("\n", load.Diagnostics));
        Assert.Equal(PackageManifestWriter.Write(load.Manifest!), built.ManifestText);
        Assert.Empty(PackageManifestValidator.Validate(load.Manifest!, "package.yaml"));
        Assert.Equal(["orders", "customers", "events"], built.Replications.Select(r => r.Replication).OrderBy(r => r == "orders" ? 0 : r == "customers" ? 1 : 2));
        Assert.All(built.Replications, r => Assert.Equal(2, r.Mappings));
    }

    [Fact]
    public void NoSecretLibraryStoreGitHistoryOrStateIsInThePackage_ButTheDefinitionAndTheLibraryScriptAre()
    {
        Library();
        Directory.CreateDirectory(Path.Combine(_root, "config", "secrets"));
        File.WriteAllText(Path.Combine(_root, "config", "secrets", "store.json"), "super-secret-pw");
        File.WriteAllText(Path.Combine(_root, "state.db"), "state");

        var built = Build(out var problems);
        var directory = Path.Combine(_root, "out");
        ReplicationSetPackage.Write(built, directory);

        Assert.Empty(problems);
        var paths = built.Files.Select(f => f.Path).ToList();
        Assert.Contains("repo/config/replications/orders/task.yaml", paths);
        Assert.Contains("repo/config/connections/crm-src.yaml", paths);
        Assert.Contains("repo/libraries/npgsql/library.csx", paths);
        Assert.DoesNotContain(paths, p => p.Contains("/secrets/") || p.Contains("/.git/") || p.EndsWith("state.db") || p.Contains("/lib/"));
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("secret-pw", File.ReadAllText(file));
        Assert.True(File.Exists(Path.Combine(directory, "package.yaml")));
        Assert.True(File.Exists(Path.Combine(directory, "files", "repo", "config", "replications", "orders", "task.yaml")));

        // plan 18 G18: the replications' design lineage, by the identity of the repository's connections, for the Runner's lineage
        var graph = DbDataKit.Lineage.PackageLineage.Read(File.ReadAllText(Path.Combine(directory, "files", "lineage", "design.json")), "lineage/design.json");
        Assert.Contains(graph.Edges, e => e.Kind == DbDataKit.Lineage.EdgeKind.Replicates && e.Detail == "orders/t1");
        Assert.All(graph.Nodes, n => Assert.Null(DbDataKit.Lineage.PackageLineage.SlotOf(n.Id)));
    }

    [Fact]
    public void FilesOfThePreviousFormatArePublishedInTheCurrentOne_AndTheFilesInTheRepositoryStayAsWritten()
    {
        // plan 33 6d, rule 5: a task file of format 1 and a connection/1 file leave as 2, whatever the repository holds
        var task = Path.Combine(_root, "config", "replications", "orders", "task.yaml");
        var connection = Path.Combine(_root, "config", "connections", "crm-src.yaml");
        var taskAsWritten = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(task), @"^schemaVersion:.*$", "schemaVersion: 1", System.Text.RegularExpressions.RegexOptions.Multiline);
        var connectionAsWritten = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(connection), @"^schema:.*$", "schema: connection/1", System.Text.RegularExpressions.RegexOptions.Multiline);
        File.WriteAllText(task, taskAsWritten);
        File.WriteAllText(connection, connectionAsWritten);

        var built = Build(out var problems);
        Assert.Empty(problems);
        var directory = Path.Combine(_root, "out");
        ReplicationSetPackage.Write(built, directory);

        var published = File.ReadAllText(Path.Combine(directory, "files", "repo", "config", "replications", "orders", "task.yaml"));
        Assert.Contains("schemaVersion: 2", published);
        Assert.DoesNotContain("schemaVersion: 1", published);
        Assert.StartsWith("schema: connection/2", File.ReadAllText(Path.Combine(directory, "files", "repo", "config", "connections", "crm-src.yaml")));
        Assert.Equal(taskAsWritten, File.ReadAllText(task));
        Assert.Equal(connectionAsWritten, File.ReadAllText(connection));
    }

    [Fact]
    public void AMappingWhoseColumnCacheIsMissingIsRefusedNamingTheMappingAndTheFile()
    {
        Replication("returns", ScheduleMode.Periodic, cron: "0 3 * * *", cached: false);

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains("t1.yaml") && p.Contains("Table mapping 't1' has no cached source column metadata"));
        Assert.Contains(problems, p => p.Contains("returns") && p.Contains("t2.yaml"));
        Assert.DoesNotContain(problems, p => p.Contains("orders"));
    }

    /// <summary>Plan 28c E21, owner decision 2026-11-15 (option 1): a target table that does not exist yet has no column cache, and the set is published all the same; the drain refuses until the
    /// table exists (<c>RunExecutorIntegrationTests.AMappingWithProvisioningOff_NeverCreatesTheTargetItInspectedFor</c>) and the set's <c>provision</c> task, which a person runs, creates it.</summary>
    [Fact]
    public void AMappingWhoseTargetTableIsNotCachedYetIsPublished_AndItsSetCarriesTheProvisionTask()
    {
        Replication("returns", ScheduleMode.Periodic, cron: "0 3 * * *", targetCached: false);

        var built = Build(out var problems);

        Assert.Empty(problems);
        Assert.NotNull(built);
        Assert.Contains(built.Manifest.Tasks, t => t.Name == ReplicationSetPackage.ProvisionTask);
        Assert.Single(built.Replications, r => r.Replication == "returns");
    }

    [Fact]
    public void ABadCronAndTwoReplicationsThatMakeOneTaskAreRefusedNamingTheFile()
    {
        Replication("broken", ScheduleMode.Periodic, cron: "not a cron");
        // two folder names that differ by more than case (a case-insensitive file system, Windows, makes Sales_Orders and sales_orders one folder) and make one task name
        Replication("sales.orders", ScheduleMode.Periodic, cron: "0 1 * * *");
        Replication("sales-orders", ScheduleMode.Periodic, cron: "0 1 * * *");

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains(Path.Combine("broken", "task.yaml")) && p.Contains("cronExpression"));
        Assert.Contains(problems, p => p.Contains("both make the task `sales-orders`"));
    }

    [Fact]
    public void AReplicationNamedForTheSetsPreflightIsRefused()
    {
        Replication("preflight", ScheduleMode.Periodic, cron: "0 1 * * *");

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains(Path.Combine("preflight", "task.yaml")) && p.Contains("the name of the set's preflight"));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("try-strategy")]
    [InlineData("discover")]
    [InlineData("describe")]
    public void AReplicationNamedForATaskOfTheSetsScreensIsRefused(string name)
    {
        Replication(name, ScheduleMode.Periodic, cron: "0 1 * * *");

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains(Path.Combine(name, "task.yaml")) && p.Contains("the name of a task the set has for its screens"));
    }

    [Fact]
    public void AReplicationNamedForTheScriptTestIsRefused()
    {
        Replication("script-test", ScheduleMode.Periodic, cron: "0 1 * * *");

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains(Path.Combine("script-test", "task.yaml")) && p.Contains("the name of the set's test of a script"));
    }

    [Fact]
    public void ASwitchedOffReplicationIsNotPublished_AndASetWithNoEnabledOneIsRefused()
    {
        Replication("off", ScheduleMode.Periodic, cron: "0 4 * * *", enabled: false);
        Assert.DoesNotContain("off", Build(out _).Manifest.Tasks.Select(t => t.Name));

        foreach (var name in new[] { "orders", "customers", "events", "off" }) Replication(name, ScheduleMode.Periodic, cron: "0 4 * * *", enabled: false);
        Assert.Null(Build(out var problems));
        Assert.Contains(problems, p => p.Contains("no enabled replication"));
    }

    [Fact]
    public void AChangeToOneReplicationChangesOnlyItsFiles_AndTheManifestStaysTheSame()
    {
        var first = Build(out _);
        var firstHashes = Hashes(first);
        Replication("orders", ScheduleMode.Periodic, cron: "*/15 * * * *");     // saved again unchanged: same bytes
        var same = Build(out _);
        Assert.Equal(first.ManifestText, same.ManifestText);
        Assert.Empty(Changed(firstHashes, Hashes(same)));

        _config.SaveTableMapping("customers", new TableMappingConfig
        {
            Name = "t1",
            Sources = [new SourceTableSpec { ConnectionName = "crm-src", Database = "App", Schema = "dbo", Table = "T1" }],
            Targets = [new TableSpec { ConnectionName = "dwh", Database = "dw", Schema = "public", Table = "renamed" }],
            SourceColumns = [new CachedColumn("id", "bigint", false, true, false, 1)],
            TargetColumns = [new CachedColumn("id", "bigint", false, true, false, 1)],
        }, Author);
        var changed = Build(out _);

        Assert.Equal(["repo/config/replications/customers/table-mappings/t1.yaml"], Changed(firstHashes, Hashes(changed)));
        Assert.Equal(first.ManifestText, changed.ManifestText);
    }

    [Fact]
    public async Task TheDriverLibrariesPartIsAddedUnderTheRepoOfThePackage()
    {
        var built = Build(out _);
        var part = Path.Combine(_root, "part");

        var withPart = await ReplicationSetPackage.AddLibrariesAsync(built, _root, part, "linux-x64");

        Assert.Contains(withPart.Files, f => f.Path == "repo/driver-libraries.json");
        var index = DbDataSync.Libraries.DriverLibrariesPart.ReadIndex(part)!;
        Assert.Equal("linux-x64", index.Platform);
        // the repository holds no library: the built-in drivers' catalog libraries its connections need are restored into the part (the deleted Sync server seeded them
        // into the repository at a connection's first open; plan 33 SC23d2), and the repository is not written
        Assert.Equal(["microsoft-data-sqlclient", "npgsql"], index.Entries.Select(e => e.Id).Order(StringComparer.Ordinal));
        Assert.Contains(withPart.Files, f => f.Path == "repo/libraries/npgsql/library.csx");
        Assert.False(Directory.Exists(Path.Combine(_root, "libraries")));
    }

    private static Dictionary<string, string> Hashes(BuiltReplicationSet set) =>
        set.Files.ToDictionary(f => f.Path, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Source))), StringComparer.Ordinal);

    private static List<string> Changed(Dictionary<string, string> a, Dictionary<string, string> b) =>
        [.. a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).Order(StringComparer.Ordinal)];
}
