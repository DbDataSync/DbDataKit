using System.Text.Json.Nodes;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataRunner.Server;

/// <summary>
/// The runner group as the API shows it (plan 28a R11, R12): the live instances of the group (inside their lease, not marked gone) with host, version, labels (each with who set it), capabilities and how many items each is running.
/// <c>GET /api/v1/instances</c> returns them; <c>GET /api/v1/status</c> carries the same list as <c>instances</c>. A viewer may read.
/// </summary>
public static class RunnerGroupApi
{
    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerEngine> engine)
    {
        app.MapGet("/api/v1/instances", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            var running = engine();
            return Results.Json(new JsonObject
            {
                ["group"] = running.Group,
                ["this"] = running.InstanceId?.ToString(),
                ["instances"] = Instances(state, running),
            });
        });
    }

    /// <summary>The live instances of the group, oldest first.</summary>
    public static JsonArray Instances(StateDatabase state, RunnerEngine engine)
    {
        var instances = new RunnerInstances(state);
        var held = new ModuleWorkQueueStore(state, RunnerState.EngineService).HeldCounts();
        var list = new JsonArray();
        foreach (var live in instances.LiveInstances(engine.Lease))
        {
            var worker = live.Capabilities.TryGetValue("worker_id", out var w) ? w : null;
            var labels = instances.Labels(live.Instance.Id);
            list.Add(new JsonObject
            {
                ["id"] = live.Instance.Id.ToString(),
                ["host"] = live.Instance.Host,
                ["version"] = live.Instance.Version,
                ["software_version"] = live.Instance.SoftwareVersion ?? live.Instance.Version,
                ["module_versions"] = ModuleVersionText.Parse(live.Instance.ModuleVersions) is { } modules
                    ? new JsonObject(modules.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => KeyValuePair.Create(m.Key, (JsonNode?)JsonValue.Create(m.Value))))
                    : null,
                ["started"] = live.Instance.StartedAt.UtcDateTime.ToString("O"),
                ["heartbeat"] = live.Instance.HeartbeatAt.UtcDateTime.ToString("O"),
                ["this"] = live.Instance.Id == engine.InstanceId,
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["source"] = l.Source }).ToArray()),
                ["capabilities"] = new JsonObject(live.Capabilities.Where(c => !RunnerEngine.InternalCapabilities.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
                ["running"] = worker is not null && held.TryGetValue(worker, out var count) ? count : 0,
            });
        }

        return list;
    }
}
