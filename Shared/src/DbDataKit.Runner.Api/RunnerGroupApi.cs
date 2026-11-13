using System.Text.Json.Nodes;
using System.Text.Json;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Runner.Api;

/// <summary>
/// The runner group as the API shows it (plan 28a R11, R12): the live instances of the group (inside their lease, not marked gone) with host, version, labels (each with who set it), capabilities and how many items each is running.
/// <c>GET /api/v1/instances</c> returns them; <c>GET /api/v1/status</c> carries the same list as <c>instances</c>. A viewer may read.
/// <para>
/// <b>Labels (plan 33, UE11).</b> <c>GET /api/v1/instances/{id}/labels</c> (a viewer) lists an instance's labels with who set each; <c>PUT /api/v1/instances/{id}/labels</c> with <c>{"labels": ["gpu", "site:oslo"]}</c> sets the administrator's labels of a
/// running instance to exactly that list (an administrator; <c>dbdatarunner label add|remove</c> over the same store calls). A label that is not written as a label, one the machine reports about itself, more than <see cref="MaxAdminLabels"/> labels, or
/// an instance that is not running is refused with the reason and nothing changes. Each label added or removed is audited (<c>runner.label.add</c>, <c>runner.label.remove</c>) and so is a refusal by role.
/// </para>
/// </summary>
public static class RunnerGroupApi
{
    public const string LabelAddVerb = "runner.label.add";
    public const string LabelRemoveVerb = "runner.label.remove";

    /// <summary>The most labels an administrator puts on one instance.</summary>
    public const int MaxAdminLabels = 32;

    private static readonly System.Text.RegularExpressions.Regex LabelRule = new(@"^[A-Za-z0-9][A-Za-z0-9_.:=-]{0,63}$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="normalized"/> (see <see cref="RunnerLabels.Normalize"/>) is a label: letters, digits and . _ : = - with no spaces, at most 64, starting with a letter or digit.</summary>
    public static bool IsLabel(string normalized) => LabelRule.IsMatch(normalized);

    public static void Map(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost> engine, string prefix, RunnerCallerResolver caller, string auditService = RunnerNames.ServiceId)
    {
        MapConnections(app, state, engine, prefix, caller);
        MapLabels(app, state, prefix, caller, auditService);
        app.MapGet($"{prefix.TrimEnd('/')}/instances", (HttpContext context) =>
        {
            var decision = caller(context).Authorize(ServerRole.Viewer);
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

    /// <summary>
    /// <c>GET {prefix}/connections</c> (plan 28c E23): the connections the group's packages are bound to and the tasks and instances that use them, in the form every product answers with (<see cref="DbDataKit.Connections.Inventory.ConnectionInventoryJson"/>). A viewer may read.
    /// </summary>
    private static void MapConnections(IEndpointRouteBuilder app, StateDatabase state, Func<RunnerHost> engine, string prefix, RunnerCallerResolver caller) =>
        app.MapGet($"{prefix.TrimEnd('/')}/connections", (HttpContext context) =>
            !caller(context).Authorize(ServerRole.Viewer).Allowed
                ? Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(RunnerConnections.Inventory(state, engine().Group, "dbdatarunner", engine().ConnectionsDirectory)));

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    private static JsonObject LabelsOf(RunnerInstances instances, RunnerInstance instance) => new()
    {
        ["instance"] = instance.Id.ToString(), ["host"] = instance.Host,
        ["labels"] = new JsonArray(instances.Labels(instance.Id).Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["source"] = l.Source }).ToArray()),
    };

    private static void MapLabels(IEndpointRouteBuilder app, StateDatabase state, string prefix, RunnerCallerResolver caller, string auditService)
    {
        var root = prefix.TrimEnd('/');
        var instances = new RunnerInstances(state);
        var audit = new AuditLog(state, auditService);

        app.MapGet($"{root}/instances/{{id}}/labels", (string id, HttpContext context) =>
        {
            if (!caller(context).Authorize(ServerRole.Viewer).Allowed) return Error(403, "This needs the viewer role.");
            return Guid.TryParse(id, out var guid) && instances.Get(guid) is { } instance ? Results.Json(LabelsOf(instances, instance)) : Error(404, $"There is no instance '{id}' in this group.");
        });

        app.MapPut($"{root}/instances/{{id}}/labels", async (string id, HttpContext context) =>
        {
            var who = caller(context);
            var decision = who.Authorize(ServerRole.Admin);
            if (!decision.Allowed)
            {
                audit.Record(new AuditEvent("person", who.Actor.Id, "runner.label.set", AuditEvent.Refused, ViaKind: who.Actor.ViaKind, ViaId: who.Actor.ViaId, TargetKind: "instance", TargetId: id,
                    Detail: JsonSerializer.Serialize(new { status = 403, reason = decision.Reason ?? "the role is not enough" })));
                return Error(403, "This needs the admin role.");
            }

            JsonNode? body;
            try { body = await context.Request.ReadFromJsonAsync<JsonNode>(context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException) { body = null; }
            if (body is not JsonObject { } json || json["labels"] is not JsonArray list || list.Any(x => x is not JsonValue v || !v.TryGetValue<string>(out _)))
                return Error(400, "The body is {\"labels\": [\"gpu\", \"site:oslo\"]}: the administrator's labels this instance should have, as texts.");
            if (!Guid.TryParse(id, out var guid) || instances.Get(guid) is not { State: RunnerInstance.Running } instance)
                return Error(404, $"There is no running instance '{id}' in this group.");

            var wanted = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in list)
            {
                var raw = (string)item!;
                var label = RunnerLabels.Normalize(raw);
                if (!IsLabel(label)) return Error(400, $"'{raw}' is not a label: letters, digits and . _ : = - with no spaces, at most 64, such as gpu or network:outside.");
                wanted.Add(label);
            }

            if (wanted.Count > MaxAdminLabels) return Error(400, $"An instance takes at most {MaxAdminLabels} labels set by an administrator; the list has {wanted.Count}.");
            var current = instances.Labels(instance.Id);
            if (wanted.FirstOrDefault(w => current.Any(c => c.Source == InstanceLabel.Reported && c.Label == w)) is { } reported)
                return Error(400, $"'{reported}' is reported by the runner itself (its capabilities) and cannot be set by hand; leave it out.");

            var have = current.Where(c => c.Source == InstanceLabel.Admin).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
            var added = wanted.Where(w => !have.Contains(w)).ToList();
            var removed = have.Where(h => !wanted.Contains(h)).Order(StringComparer.Ordinal).ToList();
            foreach (var label in added) instances.SetLabel(instance.Id, label, InstanceLabel.Admin);
            foreach (var label in removed) instances.RemoveLabel(instance.Id, label);
            foreach (var (label, verb) in added.Select(l => (l, LabelAddVerb)).Concat(removed.Select(l => (l, LabelRemoveVerb))))
                audit.Record(new AuditEvent("person", who.Actor.Id, verb, AuditEvent.Ok, ViaKind: who.Actor.ViaKind, ViaId: who.Actor.ViaId, TargetKind: "instance", TargetId: instance.Id.ToString(), Detail: JsonSerializer.Serialize(new { label })));

            var answer = LabelsOf(instances, instance);
            answer["added"] = new JsonArray(added.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
            answer["removed"] = new JsonArray(removed.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
            return Results.Json(answer);
        });
    }

    /// <summary>The live instances of the group, oldest first; with <paramref name="includeGone"/> also the ones that are gone or lapsed, marked <c>live: false</c> (their <c>state</c> says Gone or Running with a stale heartbeat).</summary>
    public static JsonArray Instances(StateDatabase state, RunnerHost engine, bool includeGone = false)
    {
        var instances = new RunnerInstances(state);
        var held = new ModuleWorkQueueStore(state, RunnerNames.EngineService).HeldCounts();
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
                ["software_version"] = instance.SoftwareVersion ?? instance.Version,
                ["module_versions"] = ModuleVersionText.Parse(instance.ModuleVersions) is { } modules
                    ? new JsonObject(modules.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => KeyValuePair.Create(m.Key, (JsonNode?)JsonValue.Create(m.Value))))
                    : null,
                ["started"] = instance.StartedAt.UtcDateTime.ToString("O"),
                ["heartbeat"] = instance.HeartbeatAt.UtcDateTime.ToString("O"),
                ["this"] = instance.Id == engine.InstanceId,
                ["live"] = isLive,
                ["state"] = instance.State,
                ["labels"] = new JsonArray(labels.Select(l => (JsonNode)new JsonObject { ["label"] = l.Label, ["source"] = l.Source }).ToArray()),
                ["capabilities"] = new JsonObject(capabilities.Where(c => !RunnerHost.InternalCapabilities.Contains(c.Key)).OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => KeyValuePair.Create(c.Key, (JsonNode?)JsonValue.Create(c.Value)))),
                ["running"] = worker is not null && held.TryGetValue(worker, out var count) ? count : 0,
            });
        }

        return list;
    }
}
