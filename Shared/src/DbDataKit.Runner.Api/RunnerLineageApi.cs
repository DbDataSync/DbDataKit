using DbDataKit.Lineage;
using DbDataKit.Runner.Deployments;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The runner's lineage (plan 28c E24): <c>GET /api/v1/lineage</c> is the deployment graph of the group (<see cref="RunnerDeploymentLineage"/>) in the wire form every server's lineage has (<see cref="LineageJson"/>), so the console joins it
/// with the others, plus the outside systems the tasks reported reading and writing (plan 27a IX12; <c>problems</c> names a task left out) and the design lineage the packages carry, with the runs along it (plan 18 G18: Build's models and column edges, Sync's replications). A viewer may read. A graph that cannot be built is a 500 saying why, not a half graph.
/// </summary>
public static class RunnerLineageApi
{
    /// <param name="connectionOf">The runner's connection of a name (its catalog), which names the tables of a package's design lineage by the connection a channel binds to their slot (plan 18 G18); null names them by the slot.</param>
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost> engine, string prefix, RunnerCallerResolver caller, Func<string, DbDataKit.Connections.ConnectionDefinition?>? connectionOf = null)
    {
        app.MapGet($"{prefix.TrimEnd('/')}/lineage", (HttpContext context) =>
        {
            if (!caller(context).Authorize(ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            var running = engine();
            try
            {
                var (graph, problems, runs) = RunnerDeploymentLineage.BuildWithDesign(state, running.Group, running.Lease, connectionOf);
                var document = LineageJson.Write(graph);
                if (runs.Count > 0) document["runs"] = LineageJson.WriteRuns(runs);
                if (problems.Count > 0) document["problems"] = new System.Text.Json.Nodes.JsonArray([.. problems.Select(p => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(p)!)]);
                return Results.Json(document);
            }
            catch (LineageGraphException ex) { return Results.Json(new { error = $"The deployment lineage cannot be built: {ex.Message}" }, statusCode: StatusCodes.Status500InternalServerError); }
        });
    }
}
