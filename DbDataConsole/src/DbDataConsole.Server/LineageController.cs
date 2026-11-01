using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DbDataConsole.Server;

/// <summary>
/// The suite's lineage (plan 18, G09): the graphs of the Build and Sync servers joined by identity. <c>GET /api/lineage</c> is the whole graph with what each server said; <c>?node=&amp;direction=downstream|upstream&amp;depth=</c>
/// is the impact or the provenance of one node; <c>?refresh=true</c> asks the servers again instead of using what was kept for half a minute.
/// </summary>
[ApiController]
[Route("api/lineage")]
public sealed class LineageController(ConsoleLineage lineage) : ControllerBase
{
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
