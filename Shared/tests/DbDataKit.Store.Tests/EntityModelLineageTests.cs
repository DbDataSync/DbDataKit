using DbDataKit.Connections;
using DbDataKit.DataModel;
using DbDataKit.Lineage;

namespace DbDataKit.Store.Tests;

/// <summary>Plan 18 G12: an entity model is datasets, columns and a serving node in the lineage graph, under the identity another product gives the same table, with what feeds it shown as unknown.</summary>
public sealed class EntityModelLineageTests
{
    private const string Model = """
        schemaVersion: 1
        name: sales_territories
        title: Sales territories
        storage: {connection: mdm_sql}
        entities:
          territory:
            key: [territory_code]
            attributes:
              territory_code: {type: VARCHAR(10), required: true}
              name:           {type: VARCHAR(100), required: true}
              parent_code:    {type: VARCHAR(10), references: {entity: territory}}
          territory_share:
            key: [territory_code, region]
            attributes:
              territory_code: {type: VARCHAR(10), references: {entity: territory}}
              region:         {type: VARCHAR(40)}
        """;

    private static readonly ConnectionDefinition Mdm = new() { Name = "mdm_sql", Engine = "sqlserver", Address = new AddressSpec { Host = "SQL01", Database = "Mdm" } };

    private static DbDataKit.DataModel.DataModel Load()
    {
        var loaded = DataModelLoader.Load(Model, "sales_territories.yaml");
        Assert.True(loaded.Ok, string.Join("; ", loaded.Diagnostics.Select(d => d.Message)));
        return loaded.Model!;
    }

    [Fact]
    public void Each_entity_is_a_table_node_with_its_columns_served_by_the_model_and_what_feeds_it_is_a_gap()
    {
        var graph = EntityModelLineage.Design(Load(), Mdm, "store-1");

        var territory = LineageIdentity.Dataset(Mdm, "sales_territories", "territory").ToString();
        Assert.Equal("sqlserver://sql01/Mdm.sales_territories.territory", territory);
        var node = graph.Node(territory)!;
        Assert.Equal((NodeKind.Dataset, true, "sales_territories.territory", "sales_territories"), (node.Kind, node.UnknownUpstream, node.Name, node.PropertyMap["entity_model"]));
        Assert.Equal(["name", "parent_code", "territory_code"], graph.Nodes.Where(n => n.Kind == NodeKind.Column && n.Id.StartsWith(territory + "#", StringComparison.Ordinal)).Select(n => n.Name).Order());
        Assert.All(graph.Nodes.Where(n => n.Kind == NodeKind.Column), n => Assert.True(n.UnknownUpstream));
        var served = graph.Node("suite://store-1/entity-model/sales_territories")!;
        Assert.Equal((NodeKind.Serving, "Sales territories"), (served.Kind, served.Name));
        Assert.Equal(2, graph.Upstream(served.Id).Count(e => e is { Kind: EdgeKind.Serves, Confidence: Confidence.Declared }));
    }

    [Fact]
    public void The_table_is_the_node_a_sync_target_on_the_same_server_and_database_is()
    {
        var graph = EntityModelLineage.Design(Load(), Mdm, "store-1");
        var sync = new ConnectionDefinition { Name = "mdm", Engine = "sqlserver", Address = new AddressSpec { Host = "sql01", Port = 1433, Database = "Mdm" } };

        Assert.NotNull(graph.Node(LineageIdentity.Dataset(sync, "sales_territories", "territory_share").ToString()));
        Assert.StartsWith("sqlserver://sql01/", Assert.Single(graph.Nodes, n => n.Name == "sales_territories.territory").Id);
        _ = LineageJson.Read(LineageJson.Write(graph));            // and it is a graph the wire form carries
    }
}
