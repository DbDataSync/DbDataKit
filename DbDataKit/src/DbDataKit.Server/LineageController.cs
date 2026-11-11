using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataKit.Server;

/// <summary>
/// The suite's lineage (plan 18, G09): the graphs of the Build and Sync servers joined by identity. <c>GET /api/lineage</c> is the whole graph with what each server said; <c>?node=&amp;direction=downstream|upstream&amp;depth=</c>
/// is the impact or the provenance of one node; <c>?refresh=true</c> asks the servers again instead of using what was kept for half a minute.
/// </summary>
[ApiController]
[Route("api/lineage")]
public sealed class LineageController(ConsoleLineage lineage) : ControllerBase
{
    /// <summary>
    /// Where definitions are deployed (plan 28c E24): <c>?definition=&lt;name or kind/name&gt;[&amp;channel=]</c> answers "where is this deployed" (channels with active, previous and staged version, who and when, the runners with their versions and states),
    /// <c>?runner=&lt;instance id or host&gt;</c> answers "what is deployed on this runner", and with neither the answer is the list of definitions and runners the runners of the console's servers have. The graph is the joined one, from the paired runners.
    /// </summary>
    [HttpGet("deployments")]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Deployments([FromQuery] string? definition, [FromQuery] string? channel, [FromQuery] string? runner, [FromQuery] bool refresh, CancellationToken cancellationToken)
    {
        var joined = await lineage.JoinedAsync(refresh, cancellationToken);
        var servers = joined.Servers.Where(s => s.Product == ServerProducts.Runner).Select(s => new { server = s.Server, error = s.Error, nodes = s.Graph?.Nodes.Count }).ToList();
        var graph = joined.Graph;
        if (definition is not null)
        {
            var found = graph.WhereDeployed(definition, channel);
            return found.Count == 0 ? NotFound(new { error = $"'{definition}' is not deployed on any runner the console's servers know.", servers }) : Ok(new { definition, channels = found.Select(DeploymentDocuments.Of).ToList(), servers });
        }
        if (runner is not null)
        {
            var found = graph.OnRunner(runner);
            return found.Count == 0 ? NotFound(new { error = $"No runner '{runner}' (an instance id or a host name).", servers }) : Ok(new { runner, instances = found.Select(DeploymentDocuments.Of).ToList(), servers });
        }
        var definitions = graph.Nodes.Where(n => n.Kind == NodeKind.Deployment && n.PropertyMap.GetValueOrDefault(DeploymentProperties.Type) == DeploymentTypes.Package)
            .Select(n => new { kind = n.PropertyMap.GetValueOrDefault(DeploymentProperties.Kind), name = n.PropertyMap.GetValueOrDefault(DeploymentProperties.Name) }).OrderBy(d => d.name, StringComparer.Ordinal).ToList();
        var runners = graph.Nodes.Where(n => n.Kind == NodeKind.Deployment && n.PropertyMap.GetValueOrDefault(DeploymentProperties.Type) == DeploymentTypes.Instance)
            .Select(n => new { instance = n.PropertyMap.GetValueOrDefault(DeploymentProperties.InstanceId), host = n.PropertyMap.GetValueOrDefault(DeploymentProperties.Host), live = n.PropertyMap.GetValueOrDefault(DeploymentProperties.Live) != "false" })
            .OrderBy(r => r.host, StringComparer.Ordinal).ToList();
        return Ok(new { definitions, runners, servers });
    }

    [HttpGet]
    [Authorize(Policies.Viewer)]
    public async Task<IActionResult> Get([FromQuery] bool refresh, [FromQuery] string? node, [FromQuery] string? direction, [FromQuery] int? depth, CancellationToken cancellationToken)
    {
        if (depth is < 1) return BadRequest(new { error = "depth is a whole number of at least 1." });
        var joined = await lineage.JoinedAsync(refresh, cancellationToken);
        var document = LineageJson.Write(joined.Graph);
        document["fingerprint"] = joined.Graph.Fingerprint();
        document["servers"] = new JsonArray(joined.Servers.Select(s => (JsonNode)new JsonObject
        {
            ["server"] = s.Server, ["product"] = s.Product, ["nodes"] = s.Graph?.Nodes.Count, ["edges"] = s.Graph?.Edges.Count,
            ["problems"] = new JsonArray(s.Problems.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()), ["error"] = s.Error, ["at"] = s.At,
        }).ToArray());
        if (joined.JoinError is not null) document["error"] = joined.JoinError;

        if (node is null) return Ok(document);
        if (joined.Graph.Node(node) is null) return NotFound(new { error = $"There is no node '{node}' in the joined graph." });
        if (direction is not (null or "downstream" or "upstream")) return BadRequest(new { error = "direction is downstream (impact) or upstream (provenance)." });
        var slice = direction == "upstream" ? joined.Graph.Provenance(node, depth ?? LineageQueries.DefaultDepth) : joined.Graph.Impact(node, depth ?? LineageQueries.DefaultDepth);
        return Ok(new JsonObject
        {
            ["start"] = slice.Start.Id, ["direction"] = direction ?? "downstream", ["truncated"] = slice.Truncated,
            ["reached"] = new JsonArray(slice.Reached.Select(r => (JsonNode)new JsonObject { ["id"] = r.Node.Id, ["name"] = r.Node.Name, ["kind"] = r.Node.Kind.ToString().ToLowerInvariant(), ["depth"] = r.Depth }).ToArray()),
            ["gaps"] = new JsonArray(slice.Gaps.Select(g => (JsonNode)JsonValue.Create(g.Id)!).ToArray()),
            ["servers"] = document["servers"]!.DeepClone(),
        });
    }
}
