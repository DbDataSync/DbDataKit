using DbDataKit.Connections;
using DbDataKit.DataModel;
using DbDataKit.Lineage;

namespace DbDataKit.Store;

/// <summary>
/// An entity model in the suite's lineage graph (plan 18, G12), in the shared types and the identity <see cref="LineageIdentity"/> gives a table, so the table a Store model keeps, the table a Build model reads from it and the table the Gateway serves are one node.
/// What the model says: a <c>dataset</c> node for each entity (the table <c>&lt;model&gt;.&lt;entity&gt;</c> on the model's connection), a <c>column</c> node for each attribute, and a <c>serving</c> node for the model
/// (<c>suite://&lt;instance&gt;/entity-model/&lt;name&gt;</c>) that each entity table <c>serves</c>. What feeds an entity is people and applications writing validated rows, which no definition says, so an entity and its columns have an <em>unknown upstream</em>:
/// a gap, shown as one. A model that keeps instances in schemas or on connections of their own is drawn for its own copy (<c>default</c>) only.
/// </summary>
public static class EntityModelLineage
{
    /// <param name="model">The model.</param>
    /// <param name="connection">The connection its tables are on, as lineage identifies it (its engine and address; no secret is read).</param>
    /// <param name="instanceId">The server that names the model's serving node.</param>
    /// <param name="connectionName">What the model's <c>storage.connection</c> calls it, kept on each table as <c>connection</c>.</param>
    public static LineageGraph Design(DataModel.DataModel model, ConnectionDefinition connection, string instanceId, string? connectionName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(connection);
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var served = LineageIdentity.SuiteObject(instanceId, "entity-model", model.Name);
        nodes.Add(new LineageNode(served, NodeKind.Serving, model.Title ?? model.Name, UnknownUpstream: false,
            new Dictionary<string, string> { ["serving"] = "entity-model", ["entity_model"] = model.Name, ["approval"] = model.Approval.ToString().ToLowerInvariant() }));
        foreach (var entity in model.Entities)
        {
            var dataset = LineageIdentity.Dataset(connection, model.Name, entity.Name);
            nodes.Add(new LineageNode(dataset.ToString(), NodeKind.Dataset, $"{model.Name}.{entity.Name}", UnknownUpstream: true,
                new Dictionary<string, string> { ["connection"] = connectionName ?? connection.Name, ["entity_model"] = model.Name, ["entity"] = entity.Name, ["role"] = entity.Role.ToString().ToLowerInvariant(), ["history"] = entity.History.ToString().ToLowerInvariant() }));
            foreach (var attribute in entity.Attributes)
                nodes.Add(new LineageNode(LineageIdentity.Column(connection, dataset, attribute.Name), NodeKind.Column, attribute.Name, UnknownUpstream: true));
            edges.Add(new LineageEdge(dataset.ToString(), served, EdgeKind.Serves, Granularity.Table, Confidence.Declared));
        }
        return new LineageGraph(nodes, edges);
    }
}
