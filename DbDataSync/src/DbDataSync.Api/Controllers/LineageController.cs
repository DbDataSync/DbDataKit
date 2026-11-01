using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataSync.Api.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataSync.Api.Controllers;

/// <summary>
/// Sync's design lineage (plan 18, G07 and G09): what its replications say can flow, as a graph in the form every server sends a console (<see cref="LineageJson"/>), so the console can join it with the other servers' by identity.
/// A replication or mapping that cannot be read is left out and named in <c>problems</c>; it does not take the rest with it.
/// </summary>
[ApiController]
[Route("api/lineage")]
public sealed class LineageController(ConfigRepository configRepository) : ControllerBase
{
    [Authorize(Policies.Viewer)]
    [HttpGet]
    public ActionResult<JsonObject> Get()
    {
        var problems = new List<string>();
        var connections = new Dictionary<string, ConnectionConfig>(StringComparer.Ordinal);
        try { foreach (var connection in configRepository.LoadAllConnections()) connections[connection.Name] = connection; }
        catch (Exception ex) when (ex is ConfigFileException or ConfigValidationException) { problems.Add($"The connections cannot be read ({ex.GetType().Name}), so no replication has a source or a target in the graph."); }

        var replications = new List<SyncReplication>();
        foreach (var name in configRepository.ListReplications())
        {
            try
            {
                var task = configRepository.LoadReplicationTask(name);
                var mappings = configRepository.ListTableMappings(name).Select(m => configRepository.LoadTableMapping(name, m)).ToList();
                replications.Add(new SyncReplication(task, mappings));
            }
            catch (Exception ex) when (ex is ConfigFileException or ConfigValidationException or MappingLoadingException)
            {
                // the type says what went wrong and not the message, which can quote a value
                problems.Add($"Replication '{name}' is left out: its configuration could not be read ({ex.GetType().Name}).");
            }
        }

        LineageGraph graph;
        try { graph = SyncLineage.Design(replications, connections, problems); }
        catch (LineageGraphException ex) { return UnprocessableEntity(new JsonObject { ["error"] = $"The replications' lineage cannot be joined: {ex.Message}" }); }
        var document = LineageJson.Write(graph);
        document["problems"] = new JsonArray(problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        return Ok(document);
    }
}
