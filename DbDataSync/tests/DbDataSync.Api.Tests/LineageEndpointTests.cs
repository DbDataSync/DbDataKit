using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Lineage;
using DbDataSync.Core.Config;
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
    }
}
