using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>
/// The Store's part of the suite's lineage graph (plan 18, G12): for each model the server holds, its entities as table nodes with their columns and a serving node for the model (<see cref="EntityModelLineage"/>), under the identity of the table on the
/// connection the model's storage names, so a Build model that reads a Store table, a Sync replication that copies it and the Gateway that serves the model meet it as one node. What is left out is named: a model that does not load, and a model whose
/// connection file is missing or is not a connection. No login is read: the identity is the address in the connection file. <c>GET /api/v1/lineage</c> (viewer) sends it in the wire form every server's lineage has. A model's connection usually names its login by a secret reference: the address is read from what it resolves to (the address keys only; nothing else is kept).
/// </summary>
public static class StoreLineage
{
    public static async Task<(LineageGraph Graph, List<string> Problems)> BuildAsync(string directory, string instanceId, StoreConnections connections, CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        var graphs = new List<LineageGraph>();
        foreach (var name in StoreModels.Names(directory).Order(StringComparer.Ordinal))
        {
            try
            {
                var loaded = StoreModels.Check(name, File.ReadAllText(StoreModels.FileOf(directory, name)));
                if (loaded.Model is not { } model || loaded.Diagnostics.Any(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error)) { problems.Add($"Model '{name}' is left out: its file is not a valid model (`model check` says why)."); continue; }
                var file = StoreConnections.FileOf(directory, model.Connection);
                if (!StoreModels.IsValidName(model.Connection) || !File.Exists(file)) { problems.Add($"Model '{name}' is left out: its storage names the connection '{model.Connection}', which has no file connections/{model.Connection}.yaml."); continue; }
                var (connection, problem) = await connections.AddressOnlyAsync(model.Connection, cancellationToken);
                if (connection is null) { problems.Add($"Model '{name}' is left out: {problem}"); continue; }
                graphs.Add(EntityModelLineage.Design(model, connection, instanceId, model.Connection));
            }
            catch (Exception ex) when (ex is IOException or LineageGraphException or UnauthorizedAccessException)
            {
                problems.Add($"Model '{name}' is left out: its lineage could not be built ({ex.GetType().Name}).");
            }
        }
        try { return (graphs.Count == 0 ? LineageGraph.Empty : LineageGraph.Merge(graphs), problems); }
        catch (LineageGraphException ex) { problems.Add($"The models' lineage cannot be joined: {ex.Message}"); return (LineageGraph.Empty, problems); }
    }

    public static void Map(IEndpointRouteBuilder app, string directory, string instanceId, StoreConnections connections) =>
        app.MapGet("/api/v1/lineage", async (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
            var (graph, problems) = await BuildAsync(directory, instanceId, connections, context.RequestAborted);
            var document = LineageJson.Write(graph);
            document["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)JsonValue.Create(p)!)]);
            return Results.Json(document);
        });
}
