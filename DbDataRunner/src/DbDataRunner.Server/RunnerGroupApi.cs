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
            // all=true adds the instances that are gone or lapsed (live: false), which the console shows as such
            var all = context.Request.Query["all"].Count > 0 && bool.TryParse(context.Request.Query["all"][0], out var wanted) && wanted;
            return Results.Json(new JsonObject
            {
                ["group"] = running.Group,
                ["this"] = running.InstanceId?.ToString(),
                ["instances"] = Instances(state, running, all),
            });
        });
    }

    /// <summary>The live instances of the group, oldest first; with <paramref name="includeGone"/> also the ones that are gone or lapsed, marked <c>live: false</c> (their <c>state</c> says Gone or Running with a stale heartbeat).</summary>
    public static JsonArray Instances(StateDatabase state, RunnerEngine engine, bool includeGone = false)
    {
        var instances = new RunnerInstances(state);
        var held = new ModuleWorkQueueStore(state, RunnerState.EngineService).HeldCounts();
        var list = new JsonArray();
        var liveIds = instances.LiveInstances(engine.Lease);
        var rows = liveIds.Select(l => (Instance: l.Instance, Live: true, Capabilities: (IReadOnlyDictionary<string, string>)l.Capabilities)).ToList();
        if (includeGone)
        {
            var liveSet = liveIds.Select(l => l.Instance.Id).ToHashSet();
            rows.AddRange(instances.ListAll().Where(i => !liveSet.Contains(i.Id)).Select(i => (Instance: i, Live: false,
                Capabilities: (IReadOnlyDictionary<string, string>)instances.Capabilities(i.Id).ToDictionary(c => c.Name, c => c.Value, StringComparer.Ordinal))));
            rows = [.. rows.OrderBy(r => r.Instance.StartedAt).ThenBy(r => r.Instance.Id)];
        }

        foreach (var (instance, isLive, capabilities) in rows)
        {
            var worker = capabilities.TryGetValue("worker_id", out var w) ? w : null;
            var labels = instances.Labels(instance.Id);
            list.Add(new JsonObject
            {
                ["id"] = instance.Id.ToString(),
                ["host"] = instance.Host,
                ["version"] = instance.Version,
                ["started"] = instance.StartedAt.UtcDateTime.ToString("O"),
                ["heartbeat"] = instance.HeartbeatAt.UtcDateTime.ToString("O"),
                ["this"] = instance.Id == engine.InstanceId,
                ["live"] = isLive,
                ["state"] = instance.State,
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["source"] = l.Source }).ToArray()),
                ["capabilities"] = new JsonObject(capabilities.Where(c => !RunnerEngine.InternalCapabilities.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
                ["running"] = worker is not null && held.TryGetValue(worker, out var count) ? count : 0,
            });
        }

        return list;
    }
}
