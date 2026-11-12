using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>Plan 18 G07b: <c>dbdatasync lineage graph|impact|provenance</c> reads the replications of the repository offline and answers the documents the noun's schema declares.</summary>
public sealed class LineageCommandTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-lineage-cli-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);   // a git repository has read-only object files (Windows refuses to delete them)

    private void Seed()
    {
        var config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.OpenInRepository(_root));
        config.SaveConnection(new ConnectionInput { Name = "crm", DriverType = DriverIds.MsSql, Host = "sql01", Database = "Crm", AuthMode = AuthMode.IntegratedAuth }, Author);
        config.SaveConnection(new ConnectionInput { Name = "dwh", DriverType = DriverIds.Postgres, Host = "pg01", Database = "dwh", AuthMode = AuthMode.IntegratedAuth }, Author);
        config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = "crm-to-dwh",
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "BatchReload" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = "crm", Database = "Crm" }, Target = new EndpointRef { ConnectionName = "dwh", Database = "dwh" } },
        }, Author);
        config.SaveTableMapping("crm-to-dwh", new TableMappingConfig
        {
            Name = "customers",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Customers" }],
            Targets = [new TableSpec { Schema = "public", Table = "customers" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "CustomerId", TargetColumn = "customer_id" }],
        }, Author);
    }

    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await SyncCli.Router().RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private static JsonNode Data(string output) => JsonNode.Parse(output)!["data"] ?? JsonNode.Parse(output)!;

    [Fact]
    public async Task The_graph_of_the_repository_is_a_document_with_the_replication_as_an_edge_and_the_impact_of_a_source_column_reaches_the_target()
    {
        Seed();
        var (exit, output, _) = await Run("lineage", "graph", "--repo", _root, "--format", "json");

        Assert.Equal(0, exit);
        Assert.Contains(Data(output)["edges"]!.AsArray(), e => (string?)e!["kind"] == "replicates" && (string?)e["granularity"] == "table" && (string?)e["detail"] == "crm-to-dwh/customers");

        var column = Data(output)["nodes"]!.AsArray().Select(n => (string)n!["id"]!).Single(id => id.EndsWith("#customerid", StringComparison.OrdinalIgnoreCase));
        var (impactExit, impact, _) = await Run("lineage", "impact", column, "--repo", _root, "--format", "json");
        Assert.Equal(0, impactExit);
        Assert.Contains("#customer_id", impact);
    }

    [Fact]
    public async Task A_replication_that_cannot_be_read_is_named_and_left_out_and_the_rest_still_answers()
    {
        Seed();
        File.WriteAllText(Path.Combine(_root, "config", "replications", "crm-to-dwh", "task.yaml"), "not: [valid");
        var (exit, output, _) = await Run("lineage", "graph", "--repo", _root, "--format", "json");

        Assert.Equal(0, exit);
        // with --format json what the text form writes to standard error is the document's `errors`
        Assert.Contains(JsonNode.Parse(output)!["errors"]!.AsArray(), e => ((string?)e)!.Contains("Replication 'crm-to-dwh' is left out", StringComparison.Ordinal));
        Assert.Empty(Data(output)["edges"]!.AsArray());
    }
}
