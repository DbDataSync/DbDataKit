using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Lineage;
using DbDataSync.Core.Config;
using DbDataSync.Api;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>`GET /api/lineage` (plan 18, G09): the replications as a graph a console can join with the other servers'.</summary>
public sealed class LineageEndpointTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly HttpClient _client = factory.CreateClient();

    private ConfigRepository Config => factory.Services.GetRequiredService<ConfigRepository>();

    [Fact]
    public async Task A_replication_is_served_as_edges_from_its_source_table_to_its_target_table()
    {
        var name = $"lineage-{Guid.NewGuid():N}";
        Config.SaveConnection(new ConnectionInput { Name = $"src-{name}", DriverType = DriverIds.MsSql, Host = "sql01", Database = "Crm", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveConnection(new ConnectionInput { Name = $"tgt-{name}", DriverType = DriverIds.Postgres, Host = "pg01", Database = "dwh", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "BatchReload" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = $"src-{name}", Database = "Crm" }, Target = new EndpointRef { ConnectionName = $"tgt-{name}", Database = "dwh" } },
        }, Author);
        Config.SaveTableMapping(name, new TableMappingConfig
        {
            Name = "customers",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Customers" }],
            Targets = [new TableSpec { Schema = "public", Table = "customers" }],
            ColumnMappings = [new ColumnMapping { SourceColumn = "CustomerId", TargetColumn = "customer_id" }],
        }, Author);

        var response = await _client.GetAsync("/api/lineage");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var graph = LineageJson.Read(document);
        var edge = Assert.Single(graph.Edges, e => e.Granularity == Granularity.Table && e.Detail == $"{name}/customers");
        Assert.Equal(EdgeKind.Replicates, edge.Kind);
        Assert.StartsWith("sqlserver://sql01", edge.From);
        Assert.StartsWith("postgres://pg01", edge.To);
        Assert.Contains(graph.Edges, e => e.Granularity == Granularity.Column && e.To.EndsWith("#customer_id"));
        Assert.IsType<JsonArray>(document["problems"]);
        // plan 18 G07b: the wire form carries `runs` (none here: nothing ran) and the graph is kept in Sync's state under its owner, the version being the graph's fingerprint
        Assert.Empty(LineageJson.ReadRuns(document));
        var store = factory.Services.GetRequiredService<LineageStore>();
        Assert.Equal(graph.Fingerprint(), store.Version(Controllers.LineageController.Owner));
        Assert.Contains(store.Load(Controllers.LineageController.Owner).Edges, e => e.Detail == $"{name}/customers");
    }

    /// <summary>Plan 18 G10b: the newest run of a mapping is on the wire along its edge, and a replication that loops with a frequency is late when its last run is more than two passes old.</summary>
    [Fact]
    public async Task The_last_run_of_a_mapping_is_sent_along_its_edge_and_is_late_after_two_passes_of_its_frequency()
    {
        var name = $"lineage-run-{Guid.NewGuid():N}";
        Config.SaveConnection(new ConnectionInput { Name = $"src-{name}", DriverType = DriverIds.MsSql, Host = "sql01", Database = "Crm", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveConnection(new ConnectionInput { Name = $"tgt-{name}", DriverType = DriverIds.Postgres, Host = "pg01", Database = "dwh", AuthMode = AuthMode.IntegratedAuth }, Author);
        Config.SaveReplicationTask(new ReplicationTaskConfig
        {
            Name = name,
            Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 1 },
            ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "BatchReload" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
            Endpoints = new TaskEndpoints { Source = new EndpointRef { ConnectionName = $"src-{name}", Database = "Crm" }, Target = new EndpointRef { ConnectionName = $"tgt-{name}", Database = "dwh" } },
        }, Author);
        Config.SaveTableMapping(name, new TableMappingConfig
        {
            Name = "customers",
            Sources = [new SourceTableSpec { Schema = "dbo", Table = "Customers" }],
            Targets = [new TableSpec { Schema = "public", Table = "customers" }],
        }, Author);
        var queue = factory.Services.GetRequiredService<DbDataSync.State.WorkQueueStore>();
        var runs = factory.Services.GetRequiredService<DbDataSync.State.TaskRunStore>();
        var id = queue.Enqueue(name, DbDataSync.State.RunKind.Primary, "customers");
        runs.BeginRun(id, pid: 1);
        runs.CompleteRun(id, DbDataSync.State.RunStatus.Succeeded, 40, 40, errorSummary: null);

        var fresh = LineageJson.ReadRuns(JsonNode.Parse(await (await _client.GetAsync("/api/lineage")).Content.ReadAsStringAsync()));
        var run = Assert.Single(fresh, r => r.Kind == EdgeKind.Replicates && r.To.Contains("customers", StringComparison.Ordinal) && r.Rows == 40);
        Assert.False(run.Late);

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        var later = LineageJson.ReadRuns(JsonNode.Parse(await (await _client.GetAsync("/api/lineage")).Content.ReadAsStringAsync()));
        Assert.True(Assert.Single(later, r => r.Rows == 40).Late);
    }
}
