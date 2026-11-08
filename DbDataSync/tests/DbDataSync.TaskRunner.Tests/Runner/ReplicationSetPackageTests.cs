using System.Security.Cryptography;
using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
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

    private void Replication(string name, ScheduleMode mode, string? cron = null, int? frequency = null, bool cached = true, bool enabled = true)
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
                TargetColumns = cached ? [new CachedColumn("id", "bigint", false, true, false, 1)] : [],
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
    public void ThreeReplicationsMakeThreeTasksWithTheirScheduleSlotsAndInputs_AndTheManifestRoundTrips()
    {
        var built = Build(out var problems);

        Assert.Empty(problems);
        Assert.Equal(["customers", "events", "orders"], built.Manifest.Tasks.Select(t => t.Name));
        var orders = built.Manifest.Tasks.Single(t => t.Name == "orders");
        Assert.Equal("*/15 * * * *", orders.Schedule.Cron);
        Assert.Equal(TimeSpan.FromSeconds(30), built.Manifest.Tasks.Single(t => t.Name == "events").Schedule.Every);
        Assert.Equal(["crm_src", "dwh"], orders.Connections.Select(c => c.Name));
        Assert.Equal(["sqlserver", "postgres"], orders.Connections.Select(c => c.Kind));     // the engine ids the runner's connections have
        Assert.All(orders.Connections, c => Assert.Equal(SlotEnvironmentFormats.SyncSecret, c.Env));
        Assert.Equal("orders", orders.Inputs.Single(i => i.Name == "replication").Default);
        Assert.Equal(ReplicationSetPackage.RepoDir, orders.Inputs.Single(i => i.Name == "repo_dir").Default);
        Assert.Equal(1, orders.Limits.Concurrency);
        Assert.Equal((ReplicationSetPackage.RuntimeName, "2026.11.x"), (built.Manifest.Launch.Runtime!.Name, built.Manifest.Launch.Runtime.Range));

        // the checkpoint of the drain is declared, once per task, as the engine keeps it
        Assert.Contains("watermark", built.ManifestText);
        Assert.All(built.Manifest.Tasks, t => Assert.NotEqual(DbDataKit.State.ProgressDeclaration.Empty, t.Progress));

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

    [Fact]
    public void ABadCronAndTwoReplicationsThatMakeOneTaskAreRefusedNamingTheFile()
    {
        Replication("broken", ScheduleMode.Periodic, cron: "not a cron");
        Replication("Sales_Orders", ScheduleMode.Periodic, cron: "0 1 * * *");
        Replication("sales_orders", ScheduleMode.Periodic, cron: "0 1 * * *");

        var built = Build(out var problems);

        Assert.Null(built);
        Assert.Contains(problems, p => p.Contains(Path.Combine("broken", "task.yaml")) && p.Contains("cronExpression"));
        Assert.Contains(problems, p => p.Contains("both make the task `sales_orders`"));
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
        Assert.Equal(built.Files.Count + 1, withPart.Files.Count);
        Assert.Equal("linux-x64", DbDataSync.Libraries.DriverLibrariesPart.ReadIndex(part)!.Platform);
    }

    private static Dictionary<string, string> Hashes(BuiltReplicationSet set) =>
        set.Files.ToDictionary(f => f.Path, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f.Source))), StringComparer.Ordinal);

    private static List<string> Changed(Dictionary<string, string> a, Dictionary<string, string> b) =>
        [.. a.Keys.Union(b.Keys).Where(k => !a.TryGetValue(k, out var x) || !b.TryGetValue(k, out var y) || x != y).Order(StringComparer.Ordinal)];
}
