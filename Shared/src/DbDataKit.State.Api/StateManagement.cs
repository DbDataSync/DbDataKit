using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.State.Cli;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>What a state call answered: an HTTP status and a JSON body.</summary>
public sealed record StateResult(int Status, JsonNode Body)
{
    public static StateResult Ok(JsonNode body) => new(200, body);
    public static StateResult Problem(int status, string error) => new(status, new JsonObject { ["error"] = error });
}

/// <summary>What a product tells the shared state endpoints.</summary>
/// <param name="Product">The product's id as it reports itself (<c>dbdatagateway</c>).</param>
/// <param name="Program">The command's name, for the sentences that name a fix (<c>dbdatagateway</c>).</param>
/// <param name="Description">What the state belongs to, for the sentences (<c>the gateway</c>).</param>
/// <param name="Target">Where the state is, as the running server has it: the settings it opened with and where each came from (<see cref="StateTarget"/>). Called for every read, so it follows the settings; it may throw <see cref="InvalidOperationException"/> naming what is wrong.</param>
/// <param name="Modules">The modules the product's state is built from.</param>
/// <param name="Scope">The group and service this process is.</param>
/// <param name="Gate">The server's maintenance mode.</param>
/// <param name="Audit">Where changes and refusals are recorded: the product's own audit.</param>
/// <param name="InstanceId">This instance's id, as management reports it.</param>
/// <param name="Version">The product's software version.</param>
/// <param name="Instances">The product's instance table; null: none.</param>
/// <param name="Clock">For tests.</param>
/// <param name="Move">True when the product keeps a settings file the state writer can write, so the server can move its own state (<c>POST /api/v1/state/move</c>, plan 29b C06). False for the console, whose own move is the command line (plan 29b QM3).</param>
/// <param name="TuneMove">For tests: changes the options of the move before it runs (a failure injected after the import, a fixed snapshot directory).</param>
public sealed record StateManagementOptions(
    string Product,
    string Program,
    string Description,
    Func<StateTarget> Target,
    IReadOnlyList<StateModule> Modules,
    StateScope Scope,
    IMaintenanceGate Gate,
    IAuditLog Audit,
    string? InstanceId = null,
    string? Version = null,
    IInstanceDirectory? Instances = null,
    TimeProvider? Clock = null,
    bool Move = false,
    Func<DbDataKit.State.Engines.Snapshots.MoveOptions, DbDataKit.State.Engines.Snapshots.MoveOptions>? TuneMove = null);

/// <summary>
/// The state endpoints of a server's management API (plan 29b, C05), apart from how a call arrives (the same answers whether a console asks over https with its token, a person asks in the console's web app, or the command line asks over the local channel):
/// <list type="bullet">
/// <item><c>GET /api/v1/state</c> (viewer): the product, the engine, the connection with secrets masked (references as written, never resolved), the namespaces and each module's version in the registry against this binary, the migrations pending, where each effective
/// setting came from (flag, environment, file, default), this instance's module versions and the other live instances where the product keeps an instance table, and the maintenance mode. Read-only: nothing is created or migrated.</item>
/// <item><c>GET /api/v1/state/maintenance</c> (viewer): <c>enabled</c>, <c>idle</c>, <c>since</c>, <c>reason</c>, <c>inflight</c>.</item>
/// <item><c>POST /api/v1/state/maintenance</c> (admin), body <c>{"enabled": true, "reason": "moving the state to PostgreSQL"}</c>: the server stops taking new work and drains; <c>idle</c> turns true when what was in flight has finished. Audited (<c>state.maintenance</c>, who, the reason), refusals too. Nothing is persisted: a restart leaves maintenance.</item>
/// <item><c>GET</c> and <c>POST /api/v1/state/move</c>: the server moves its own state to another database (admin to start; see <see cref="StateMoveRunner"/>), on a server in maintenance that reports idle, in a task of its own with progress to read; where the product has no settings file the call is 404.</item>
/// </list>
/// A secret is never part of an answer: the connection is shown as <see cref="StateStatusReader"/> shows it and the answer is scrubbed of the literal credentials of the connection string as a last step.
/// </summary>
public sealed class StateManagement(StateManagementOptions options)
{
    /// <summary>Where the state endpoints live; <see cref="MaintenanceMiddleware"/> lets everything under it through.</summary>
    public const string Prefix = "/api/v1/state";

    public const string MaintenancePath = Prefix + "/maintenance";

    public const string MovePath = Prefix + "/move";

    public const string AuditCommand = "state.maintenance";

    private const int MaxReason = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = options.Clock ?? TimeProvider.System;
    private readonly StateMoveRunner _move = new(options, (caller, command, outcome, target, at) => Record(options, caller, command, outcome, target, at), options.Clock ?? TimeProvider.System);

    public IMaintenanceGate Gate => options.Gate;

    /// <summary>True when <paramref name="route"/> (a path without its query) is one of this class's.</summary>
    public static bool Handles(string route) => route.TrimEnd('/') is Prefix or MaintenancePath or MovePath;

    public async Task<StateResult> HandleAsync(string method, string route, string? body, StateCaller caller, CancellationToken cancellationToken = default)
    {
        route = route.Split('?')[0].TrimEnd('/');
        method = method.ToUpperInvariant();
        switch (method, route)
        {
            case ("GET", Prefix):
                return caller.Holds(ServerRole.Viewer) ? await StateAsync(cancellationToken) : Denied(ServerRole.Viewer);
            case ("GET", MaintenancePath):
                return caller.Holds(ServerRole.Viewer) ? StateResult.Ok(options.Gate.Status().ToJson()) : Denied(ServerRole.Viewer);
            case ("POST", MaintenancePath):
                return Maintenance(body, caller);
            case ("GET", MovePath):
                return !_move.Enabled ? StateResult.Problem(404, "There is no such call.") : caller.Holds(ServerRole.Viewer) ? _move.Read() : Denied(ServerRole.Viewer);
            case ("POST", MovePath):
                return _move.Start(body, caller, ServerRole.Admin);
            default:
                return StateResult.Problem(route is Prefix or MaintenancePath or MovePath ? 405 : 404, route is Prefix or MaintenancePath or MovePath ? "This call is not allowed on this path." : "There is no such call.");
        }
    }

    private static StateResult Denied(ServerRole needed) => StateResult.Problem(403, $"This call needs the {needed.ToString().ToLowerInvariant()} role.");

    // ---- maintenance ----

    private StateResult Maintenance(string? body, StateCaller caller)
    {
        var at = _clock.GetUtcNow();
        if (!caller.Holds(ServerRole.Admin))
        {
            // a refused attempt is on record too, so an administrator reading the audit sees who tried
            Record(caller, "refused", "not allowed: the admin role is needed", at);
            return Denied(ServerRole.Admin);
        }

        JsonNode? parsed;
        try { parsed = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body); }
        catch (JsonException) { return Refuse(caller, "the body is not JSON", at); }
        if (parsed is not JsonObject request || request["enabled"] is not JsonValue flag || !flag.TryGetValue<bool>(out var enabled))
            return Refuse(caller, "the body is {\"enabled\": true|false, \"reason\": \"...\"}", at);

        string? reason = null;
        if (request["reason"] is { } given)
        {
            if (given is not JsonValue text || !text.TryGetValue<string>(out reason)) return Refuse(caller, "reason is text", at);
            reason = new string([.. reason.Where(c => !char.IsControl(c))]).Trim();
            if (reason.Length > MaxReason) return Refuse(caller, $"reason is at most {MaxReason} characters", at);
        }

        if (enabled && string.IsNullOrEmpty(reason)) return Refuse(caller, "a reason is needed to enter maintenance: say what it is for", at);

        var before = options.Gate.Status();
        try
        {
            if (enabled) options.Gate.Enter(reason!);
            else options.Gate.Leave();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(caller, "failed", enabled ? "enable: " + reason : "disable", at);
            return StateResult.Problem(500, $"The product's maintenance hook failed ({ex.GetType().Name}).");
        }

        try { Record(caller, "ok", enabled ? "enable: " + reason : "disable", at, rethrow: true); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // a change that cannot be recorded is not kept
            if (before.Enabled) options.Gate.Enter(before.Reason ?? "maintenance"); else options.Gate.Leave();
            return StateResult.Problem(500, $"The change could not be written to the audit ({ex.GetType().Name}), so it was not made.");
        }

        return StateResult.Ok(options.Gate.Status().ToJson());
    }

    private StateResult Refuse(StateCaller caller, string why, DateTimeOffset at)
    {
        Record(caller, "refused", "invalid: " + why, at);
        return StateResult.Problem(400, char.ToUpperInvariant(why[0]) + why[1..] + ".");
    }

    private void Record(StateCaller caller, string outcome, string target, DateTimeOffset at, bool rethrow = false) => Record(options, caller, AuditCommand, outcome, target, at, rethrow);

    private static void Record(StateManagementOptions options, StateCaller caller, string command, string outcome, string target, DateTimeOffset at, bool rethrow = false)
    {
        try { options.Audit.Append(caller.Audit(command, target, outcome, at)); }
        catch (Exception) when (!rethrow) { /* a refusal is still answered when the audit cannot be written */ }
    }

    // ---- state ----

    private async Task<StateResult> StateAsync(CancellationToken cancellationToken)
    {
        var context = new StateVerbContext(options.Program, options.Description, _ => options.Target(), options.Modules, options.Scope, [], "");
        (int exit, IDictionary<string, object?> data) = await StateStatusReader.ReadAsync(context, cancellationToken);
        var shown = JsonSerializer.SerializeToNode(data, Json)!.AsObject();

        var answer = new JsonObject
        {
            ["product"] = options.Product,
            ["software_version"] = options.Version,
            ["management_api_version"] = ManagementApi.Version,
            ["instance_id"] = options.InstanceId,
            ["read"] = exit == 0,
        };
        foreach (var (key, value) in shown.ToList())
        {
            if (key == "product") continue;
            shown.Remove(key);
            answer[key] = value;
        }

        answer["instance"] = Self(shown: answer);
        var live = (options.Instances ?? IInstanceDirectory.Empty).Live();
        var newest = GroupGenerations.Newest(live.Select(i => i.Generation));
        answer["instances"] = new JsonArray([.. live.Select(i => Instance(i, newest))]);
        answer["maintenance"] = options.Gate.Status().ToJson();
        return StateResult.Ok(Scrub(answer));
    }

    /// <summary>This instance's own module versions: what its binary has, against what the registry says is applied.</summary>
    private JsonObject Self(JsonObject shown)
    {
        var modules = new JsonArray();
        foreach (var module in shown["modules"] as JsonArray ?? [])
        {
            if (module is not JsonObject m) continue;
            modules.Add(new JsonObject
            {
                ["module"] = m["module"]?.DeepClone(), ["scope"] = m["scope"]?.DeepClone(),
                ["binary_version"] = m["binary_version"]?.DeepClone(), ["registered_version"] = m["registered_version"]?.DeepClone(),
            });
        }

        return new JsonObject { ["instance_id"] = options.InstanceId, ["software_version"] = options.Version, ["modules"] = modules };
    }

    private static JsonNode Instance(StateInstance instance, int? newest)
    {
        var node = new JsonObject { ["key"] = instance.Key, ["self"] = instance.IsSelf };
        if (instance.Host is not null) node["host"] = instance.Host;
        if (instance.ProcessId is { } pid) node["process_id"] = pid;
        if (instance.Started is { } started) node["started"] = started.ToString("O", CultureInfo.InvariantCulture);
        if (instance.Heartbeat is { } heartbeat) node["heartbeat"] = heartbeat.ToString("O", CultureInfo.InvariantCulture);
        if (instance.Version is not null) node["software_version"] = instance.Version;
        if (instance.Modules is { } modules) node["module_versions"] = new JsonObject(modules.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => KeyValuePair.Create(m.Key, (JsonNode?)JsonValue.Create(m.Value))));
        if (instance.Generation is { } generation)
        {
            node["compatibility_generation"] = generation;
            // 24a UC05: an instance more than one generation behind the group's newest is marked, with the words to say
            if (GroupGenerations.Outside(generation, newest) is { } outside) node["outside_n_minus_1"] = outside;
        }

        if (instance.DefinitionFormats is { } formats)
            node["definition_formats"] = new JsonObject(formats.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)new JsonObject { ["previous"] = f.Value.Previous, ["current"] = f.Value.Current })));
        return node;
    }

    /// <summary>
    /// The last step before an answer leaves: any literal credential of the connection string as written (a password somebody typed into the settings) is replaced. The connection is masked already; this is for a message that quotes one.
    /// </summary>
    private JsonNode Scrub(JsonObject answer)
    {
        string? written;
        try { written = options.Target().Settings.ConnectionString; }
        catch (InvalidOperationException) { return answer; }
        var secrets = ConnectionStringReferences.CredentialValues(written).Where(s => s.Length >= 3).OrderByDescending(s => s.Length).ToList();
        if (secrets.Count == 0) return answer;
        var text = answer.ToJsonString();
        foreach (var secret in secrets) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return JsonNode.Parse(text)!;
    }
}
