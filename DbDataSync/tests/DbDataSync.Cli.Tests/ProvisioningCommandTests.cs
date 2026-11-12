using System.Text.Json;
using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync provisioning plan &lt;replication&gt; --from-cache</c> (plan 33 SC22j): the plan of the target tables from the catalog cache of the repository, offline, with the statements of the product's own planners. A SQL Server source and a
/// PostgreSQL target, as the Workspace's Sync template has.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class ProvisioningCommandTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-prov-tests-").FullName;
    private readonly ConfigRepository _config;

    public ProvisioningCommandTests()
    {
        _config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
        _config.SaveConnection(new ConnectionInput { Name = "src", DriverType = DriverIds.MsSql, Host = "src", Database = "App", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "pw" }, Author);
        _config.SaveConnection(new ConnectionInput { Name = "dwh", DriverType = DriverIds.Postgres, Host = "dw", Database = "dw", AuthMode = AuthMode.SqlAuth, UserId = "u", Password = "pw" }, Author);
        Replication("orders", "MsSqlChangeTracking");
        Replication("scan", "Watermark");
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private void Replication(string name, string reader)
    {
        _config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic, CronExpression = "*/15 * * * *" },
            ChangeProcessing = new ChangeProcessingConfig
            {
                Reader = new ReaderConfig { Kind = reader },
                Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
                Writer = new WriterConfig { Kind = "PostgresMerge" },
            },
        }, Author);
        _config.SaveTableMapping(name, new TableMappingConfig
        {
            Name = "t1",
            Sources = [new SourceTableSpec { ConnectionName = "src", Database = "App", Schema = "dbo", Table = "T1" }],
            Targets = [new TableSpec { ConnectionName = "dwh", Database = "dw", Schema = "public", Table = "t1" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "id", TargetColumn = "id" }, new ColumnMapping { SourceColumn = "name", TargetColumn = "name" }],
            PrimaryKey = ["id"],
            SourceColumns = [new CachedColumn("id", "bigint", false, true, false, 1), new CachedColumn("name", "nvarchar(50)", true, false, false)],
            TargetColumns = [],
        }, Author);
    }

    private void Cache(string connections) =>
        File.WriteAllText(Path.Combine(_root, "catalog-cache.yaml"), "schema: catalog-cache/1\ngenerated_at: 2026-11-12T10:00:00Z\nsource: {server: prod-gateway, product: gateway}\nconnections:\n" + connections);

    private const string TargetWithoutTable = "  dwh:\n    engine: postgres\n    tables:\n      - schema: public\n        name: other\n        kind: table\n        columns:\n          - {name: x, type: integer, nullable: false}\n";

    private const string TargetMissingAColumn = "  dwh:\n    engine: postgres\n    tables:\n      - schema: public\n        name: t1\n        kind: table\n        columns:\n          - {name: id, type: bigint, nullable: false, key: true}\n";

    private async Task<(int Exit, JsonElement Document, string Error)> PlanAsync(string replication)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exit = await ProvisioningCommand.RunAsync(["plan", replication, "--from-cache", "--repo", _root, "--json"]);
            return (exit, output.ToString().Length == 0 ? default : JsonDocument.Parse(output.ToString()).RootElement.Clone(), error.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    [Fact]
    public async Task ATableTheCacheDoesNotHave_IsPlannedAsACreateWithTheProductsOwnStatement()
    {
        Cache(TargetWithoutTable);

        var (exit, document, error) = await PlanAsync("scan");

        Assert.True(exit == 0, error);
        var mapping = Assert.Single(document.GetProperty("mappings").EnumerateArray());
        var target = mapping.GetProperty("target_plan");
        Assert.Equal("missing", target.GetProperty("state").GetString());
        var step = Assert.Single(target.GetProperty("steps").EnumerateArray());
        Assert.Contains("CREATE TABLE", step.GetProperty("command_text").GetString());
        Assert.Contains("\"public\".\"t1\"", step.GetProperty("command_text").GetString());
        Assert.Equal("satisfied", mapping.GetProperty("source_plan").GetProperty("state").GetString());
        Assert.Equal("prod-gateway", document.GetProperty("cache_server").GetString());
    }

    [Fact]
    public async Task ATableTheCacheHasWithAColumnMissing_IsPlannedAsAnAlterThatAddsIt_AndSaysWhatTheCacheDoesNotCompare()
    {
        Cache(TargetMissingAColumn);

        var (exit, document, _) = await PlanAsync("scan");

        Assert.Equal(0, exit);
        var target = document.GetProperty("mappings")[0].GetProperty("target_plan");
        Assert.Equal("alterTargetTable", target.GetProperty("action").GetString());
        Assert.Equal("missing", target.GetProperty("state").GetString());
        Assert.Contains("ADD", target.GetProperty("steps")[0].GetProperty("command_text").GetString());
        Assert.Contains("no index, foreign key or default", target.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task ATableThatAlreadyFits_IsSatisfied()
    {
        Cache(TargetMissingAColumn.Replace("          - {name: id, type: bigint, nullable: false, key: true}\n", "          - {name: id, type: bigint, nullable: false, key: true}\n          - {name: name, type: 'character varying(50)', nullable: true}\n"));

        var (_, document, _) = await PlanAsync("scan");

        Assert.Equal("satisfied", document.GetProperty("mappings")[0].GetProperty("target_plan").GetProperty("state").GetString());
    }

    [Fact]
    public async Task AConnectionTheCacheHasNoEntryFor_IsUnknownNotCreate_AndTheSourceThatNeedsTheLiveServerSaysSo()
    {
        Cache("  other-db:\n    engine: postgres\n    tables: []\n");

        var (exit, document, _) = await PlanAsync("orders");

        Assert.Equal(0, exit);
        var mapping = document.GetProperty("mappings")[0];
        Assert.Equal("unknown", mapping.GetProperty("target_plan").GetProperty("state").GetString());
        Assert.Contains("no entry for the connection 'dwh'", mapping.GetProperty("target_plan").GetProperty("warnings")[0].GetString());
        Assert.Equal("unknown", mapping.GetProperty("source_plan").GetProperty("state").GetString());
        Assert.Contains("MsSqlChangeTracking", mapping.GetProperty("source_plan").GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task NoCacheFile_FailsNamingIt_AndAWithoutFromCacheIsUsage()
    {
        var (exit, _, error) = await PlanAsync("scan");
        Assert.Equal(1, exit);
        Assert.Contains("catalog-cache.yaml is not in", error);

        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var err = new StringWriter();
        Console.SetError(err);
        try { Assert.Equal(2, await ProvisioningCommand.RunAsync(["plan", "scan", "--repo", _root])); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
        Assert.Contains("--from-cache", err.ToString());
    }

    [Fact]
    public async Task ACacheTheStrictReaderRefuses_IsNotPlannedFrom()
    {
        File.WriteAllText(Path.Combine(_root, "catalog-cache.yaml"), "schema: catalog-cache/1\ngenerated_at: 2026-11-12T10:00:00Z\nsource: {server: s, product: gateway}\nconnections:\n  dwh:\n    engine: postgres\n    tables:\n      - schema: public\n        name: t1\n        kind: table\n        rows: []\n        columns: []\n");

        var (exit, _, error) = await PlanAsync("scan");

        Assert.Equal(1, exit);
        Assert.Contains("DBD-CAT-002", error);
    }

    [Fact]
    public async Task AReplicationThatDoesNotExist_IsNamed()
    {
        Cache(TargetWithoutTable);

        var (exit, _, error) = await PlanAsync("nope");

        Assert.Equal(1, exit);
        Assert.Contains("'nope' does not exist", error);
    }

    [Fact]
    public async Task TheRouterCarriesTheSamePlanAsDataInFormatJson()
    {
        Cache(TargetWithoutTable);
        var output = new StringWriter();

        var exit = await SyncCli.Router("1").RunAsync(["provisioning", "plan", "scan", "--from-cache", "--repo", _root, "--format", "json"], new DbDataKit.Cli.CliContext(output, new StringWriter(), TextReader.Null));

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("scan", document.RootElement.GetProperty("data").GetProperty("replication").GetString());
        Assert.Equal("missing", document.RootElement.GetProperty("data").GetProperty("mappings")[0].GetProperty("target_plan").GetProperty("state").GetString());
    }
}
