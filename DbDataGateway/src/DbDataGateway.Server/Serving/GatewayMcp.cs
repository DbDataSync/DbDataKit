using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Serving;

/// <summary>One call of a tool: who asked, and with what. A tool that runs after approval is called with the requester again, as they are then.</summary>
public sealed record McpCall(StoreCaller Caller, JsonObject Arguments, CancellationToken CancellationToken);

/// <summary>
/// A tool the <c>mcp</c> module offers (plan 20 O12). A tool that only reads runs at once, as the caller. A tool with an <see cref="Effect"/> never runs when an agent calls it: <see cref="Summary"/> says in words what it would do,
/// <see cref="Check"/> refuses a caller who could not do it anyway (so a request that could never run is not put to a person), and <see cref="Run"/> is called only after a person approved the request.
/// </summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, bool Effect, Func<McpCall, Task<JsonNode>> Run, Func<JsonObject, string>? Summary = null, Func<StoreCaller, JsonObject, string?>? Check = null);

/// <summary>What a decision on an approval answers (the route's status and body).</summary>
public sealed record McpDecision(int Status, JsonNode Body)
{
    public static McpDecision Error(int status, string message) => new(status, new JsonObject { ["error"] = message });
}

/// <summary>
/// The approvals of the <c>mcp</c> module, in the group's state (the <c>agents</c> module's <c>approval</c> and <c>audit_event</c> tables, plan 27a AG15: the same store the console's agents use, origin <c>mcp</c>): a step with an effect is written as pending
/// with who asked, the tool, the arguments and a sentence about it; it runs only when a person answers yes, and then exactly once (the first answer stands, <see cref="AgentStore.Answer"/>; a second one, or an answer after the step expired, changes nothing).
/// The person who answers is never the requester and never an agent (a service account); they hold the approver or the admin role on this server. A step nobody answers within <paramref name="window"/> is expired, and an expired or denied step never runs.
/// A step is at-most-once: when the instance stops between the answer and the run, the step is recorded as approved and was not run, and the agent asks again.
/// </summary>
public sealed class McpApprovals(StateDatabase state, Func<StoreCallers> callers, IReadOnlyDictionary<string, McpTool> tools, TimeSpan window)
{
    public const string Origin = "mcp";
    private const string Version = "mcp/1";

    /// <summary>The audit kinds, the ones the agents' audit uses (<c>AgentEventKinds</c>).</summary>
    private static class Kinds
    {
        public const string ToolCalled = "tool-called", ToolRefused = "tool-refused", ToolFailed = "tool-failed", Asked = "approval-asked", Approved = "approval-approved", Denied = "approval-denied";
    }
    private const int DetailLimit = 4000;

    private readonly AgentStore _store = new(state);

    public TimeSpan Window => window;

    /// <summary>Records the step as waiting for a person and returns its id.</summary>
    public Guid Request(StoreCaller caller, McpTool tool, JsonObject arguments)
    {
        ExpireStale();
        var summary = tool.Summary?.Invoke(arguments) ?? $"Run {tool.Name}.";
        var text = arguments.ToJsonString();
        var id = _store.RequestApproval(null, caller.UserId, caller.Name ?? caller.UserId, Version, Origin, tool.Name, summary, text, caller.IsAgent ? "write, agent" : "write");
        _store.RecordAudit(null, null, Kinds.Asked, caller.UserId, caller.Name ?? caller.UserId, Version, Origin, tool.Name, text, summary);
        return id;
    }

    /// <summary>Marks every step that waited longer than the window expired (a person's answer to it then counts for nothing).</summary>
    public int ExpireStale() => _store.ExpireApprovals(window);

    /// <summary>The steps waiting for a person, or every step, newest first; for an approver.</summary>
    public IReadOnlyList<ApprovalRow> List(bool pendingOnly)
    {
        ExpireStale();
        return [.. _store.Approvals(pendingOnly, null, 200).Where(a => a.Origin == Origin)];
    }

    /// <summary>One step of the module, or null.</summary>
    public ApprovalRow? Get(Guid id)
    {
        ExpireStale();
        return _store.Approval(id) is { Origin: Origin } row ? row : null;
    }

    public static bool MayAnswer(StoreCaller person) => !person.IsAgent && (person.Holds(ServerRole.Approver) || person.Holds(ServerRole.Admin));

    /// <summary>
    /// A person's answer. Refused (403) for an agent, for the requester and for a person without the approver or admin role; 404 for a step this module does not know; 409 when the step is not waiting any more (answered or expired).
    /// An approval runs the tool as the requester (their roles as they are now), once, and answers with what it gave.
    /// </summary>
    public async Task<McpDecision> DecideAsync(StoreCaller by, Guid id, bool approve, string? note, CancellationToken cancellationToken)
    {
        ExpireStale();
        if (_store.Approval(id) is not { Origin: Origin } row) return McpDecision.Error(404, "There is no such approval request.");
        if (by.IsAgent) return McpDecision.Error(403, "An agent cannot answer an approval request: a person does.");
        if (string.Equals(by.UserId, row.Person, StringComparison.Ordinal)) return McpDecision.Error(403, "The one who asked cannot answer their own request: another person does.");
        if (!by.Interactive) return McpDecision.Error(403, "An approval is given by a person who signed in (an access token the identity home issued), not with a personal access token or a service account's token: a token can be handed to an agent.");
        if (!MayAnswer(by)) return McpDecision.Error(403, "Answering an approval request needs the approver or admin role on this server.");
        if (row.Status != "pending") return McpDecision.Error(409, $"The request is {row.Status}; it cannot be answered any more.");
        if (!_store.Answer(id, approve, by.UserId, note, anyPerson: true)) return McpDecision.Error(409, "The request was answered or expired just now; it cannot be answered any more.");
        var who = by.Name ?? by.UserId;
        _store.RecordAudit(null, null, approve ? Kinds.Approved : Kinds.Denied, row.Person, row.Agent, row.AgentVersion, Origin, row.Tool, row.Arguments, $"{id:D}: {who} ({by.UserId}){(string.IsNullOrWhiteSpace(note) ? "" : ": " + note)}");
        if (!approve) return new McpDecision(200, new JsonObject { ["id"] = id.ToString("D"), ["status"] = "denied" });
        return new McpDecision(200, await RunAsync(row, cancellationToken));
    }

    private async Task<JsonNode> RunAsync(ApprovalRow row, CancellationToken cancellationToken)
    {
        var node = new JsonObject { ["id"] = row.Id.ToString("D"), ["status"] = "approved" };
        string Record(string kind, string detail)
        {
            _store.RecordAudit(null, null, kind, row.Person, row.Agent, row.AgentVersion, Origin, row.Tool, row.Arguments, $"{row.Id:D}: {Limit(detail)}");
            return detail;
        }
        try
        {
            if (!tools.TryGetValue(row.Tool, out var tool)) { node["ran"] = false; node["error"] = Record(Kinds.ToolFailed, $"The tool '{row.Tool}' is not offered any more."); return node; }
            if (callers().CallerFor(row.Person) is not { } requester) { node["ran"] = false; node["error"] = Record(Kinds.ToolFailed, "The one who asked is not an enabled user of the identity home any more."); return node; }
            if (tool.Check?.Invoke(requester, JsonNode.Parse(row.Arguments ?? "{}") as JsonObject ?? []) is { } refusal) { node["ran"] = false; node["error"] = Record(Kinds.ToolRefused, refusal); return node; }
            var result = await tool.Run(new McpCall(requester, JsonNode.Parse(row.Arguments ?? "{}") as JsonObject ?? [], cancellationToken));
            Record(Kinds.ToolCalled, result.ToJsonString());
            node["ran"] = true;
            node["result"] = result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            node["ran"] = false;
            node["error"] = Record(Kinds.ToolFailed, ex.Message);
        }
        return node;
    }

    private static string Limit(string text) => text.Length <= DetailLimit ? text : text[..DetailLimit] + "...";

    /// <summary>What an agent sees of its own request: the status, and once it was approved what the run gave (from the audit).</summary>
    public JsonObject StatusFor(StoreCaller asker, Guid id)
    {
        if (Get(id) is not { } row || !string.Equals(row.Person, asker.UserId, StringComparison.Ordinal)) return new JsonObject { ["error"] = "There is no such approval request of yours." };
        var node = new JsonObject { ["id"] = id.ToString("D"), ["status"] = row.Status, ["tool"] = row.Tool, ["summary"] = row.Preview };
        if (row.Status == "approved")
        {
            var prefix = id.ToString("D") + ": ";
            var outcome = _store.Audit(person: row.Person, kind: null, origin: Origin, limit: 500).Where(a => a.Detail is { } d && d.StartsWith(prefix, StringComparison.Ordinal) && a.Kind is Kinds.ToolCalled or Kinds.ToolFailed or Kinds.ToolRefused)
                .OrderByDescending(a => a.OccurredAt).FirstOrDefault();
            if (outcome is null) node["ran"] = false;
            else
            {
                node["ran"] = outcome.Kind == Kinds.ToolCalled;
                var text = outcome.Detail![prefix.Length..];
                if (outcome.Kind == Kinds.ToolCalled) node["result"] = TryParse(text); else node["error"] = text;
            }
        }
        return node;
    }

    private static JsonNode? TryParse(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }

    public static JsonObject Json(ApprovalRow row) => new()
    {
        ["id"] = row.Id.ToString("D"), ["status"] = row.Status, ["tool"] = row.Tool, ["summary"] = row.Preview, ["asked_by"] = row.Person, ["agent"] = row.Agent,
        ["arguments"] = row.Arguments is null ? null : TryParse(row.Arguments), ["asked_at"] = row.RequestedAt.ToString("O"), ["answered_at"] = row.AnsweredAt?.ToString("O"), ["answered_by"] = row.AnsweredBy, ["note"] = row.Note,
    };
}

/// <summary>The tools of the <c>mcp</c> module: what the group serves, read as the caller, and the writes that wait for a person.</summary>
public static class McpTools
{
    /// <summary>The tools over the group's serving services, semantic models, published views and entity models.</summary>
    public static IReadOnlyList<McpTool> Create(IServingServices serving, SemanticModels semantic, GroupStoreApi store, Func<GatewayViewsPublishing?> views, Func<StoreCallers> callers, IOptionsMonitor<GatewayOptions> options)
    {
        GatewayCaller CallerOf(McpCall call) =>
            callers().UserOf(call.Caller.UserId) is { } user
                ? new GatewayCaller(HomePrincipals.Create(callers(), user, "mcp", options.CurrentValue))
                : throw new ServingDeniedException("Data is for the people and service accounts of the identity home.");
        ServingContext ContextOf(McpCall call) => new(CallerOf(call));
        // a model is the caller's when every data source it reads is one of the data sources their home groups reach (what a query would be refused for otherwise)
        static bool MayRead(GatewayCaller caller, SemanticModel model) => model.Sources.Values.All(caller.Principal.CanUse);
        void MustRead(McpCall call, string name)
        {
            if (semantic.Find(SemanticModels.SourcePrefix + name) is not { } model) throw new ServingNotFoundException($"There is no semantic model '{name}' on this server.");
            if (!MayRead(CallerOf(call), model)) throw new ServingDeniedException($"Not authorized for the semantic model '{name}': its data sources are not ones your home groups reach.");
        }
        static JsonObject Schema(JsonObject properties, params string[] required) => new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]) };
        static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

        return
        [
            new("list_semantic_models", "The semantic models you may query: name, with what each offers through describe_semantic_model.", Schema([]), false, call =>
            {
                var caller = CallerOf(call);
                return Task.FromResult<JsonNode>(SemanticRest.Models(semantic, semantic.All.Where(m => MayRead(caller, m)).Select(m => m.Name)));
            }),
            new("describe_semantic_model", "The measures and attributes of a semantic model, as you may see them.", Schema(new() { ["model"] = Text("The model's name.") }, "model"), false,
                async call => { MustRead(call, Argument(call, "model")); return await SemanticRest.CatalogAsync(serving, ContextOf(call), Argument(call, "model"), call.CancellationToken); }),
            new("query_semantic", "Runs a read-only semantic query (the semantic layer's JSON request: measures, attributes, filters, limit) with your own access rules.",
                Schema(new() { ["model"] = Text("The model's name."), ["request"] = new JsonObject { ["type"] = "object", ["description"] = "The semantic request, e.g. {\"kind\":\"query\",\"measures\":[\"revenue\"],\"attributes\":[\"region\"],\"limit\":20}." } }, "model", "request"), false,
                async call => { MustRead(call, Argument(call, "model")); return await SemanticRest.QueryAsync(serving, ContextOf(call), Argument(call, "model"), call.Arguments["request"]?.ToJsonString() ?? "{}", call.CancellationToken); }),
            new("list_views", "The dashboards and reports published to this group: name and the served version.", Schema([]), false, call =>
            {
                var heads = views()?.List() ?? [];
                return Task.FromResult<JsonNode>(new JsonObject { ["views"] = new JsonArray([.. heads.OrderBy(h => h.Name, StringComparer.Ordinal).Select(h => (JsonNode)new JsonObject { ["kind"] = h.Kind, ["name"] = h.Name, ["version"] = h.Active })]) });
            }, Check: (caller, _) => caller.Holds(ServerRole.Viewer) ? null : "Listing views needs the viewer role on this server."),
            new("list_entity_models", "The master data models published to this group, with their entities.", Schema([]), false, call => Task.FromResult(Body(store.List(call.Caller)))),
            new("describe_entity_model", "The entities of a published master data model: attributes, keys, rules.", Schema(new() { ["model"] = Text("The model's name.") }, "model"), false, call => Task.FromResult(Body(store.Schema(call.Caller, Argument(call, "model"))))),
            new("read_entity_rows", "A page of rows of an entity of a published model, now or as of a date.", Schema(new() { ["model"] = Text("The model's name."), ["entity"] = Text("The entity's name."), ["limit"] = new JsonObject { ["type"] = "integer" } }, "model", "entity"), false,
                async call => Body(await store.RowsAsync(call.Caller, Argument(call, "model"), Argument(call, "entity"),
                    call.Arguments["limit"] is { } limit ? new Dictionary<string, string> { ["limit"] = limit.ToString() } : new Dictionary<string, string>(), call.CancellationToken))),
            new("write_entity_changes", "Changes master data: a batch of inserts, updates and deletes against a published model. It does NOT run now: it asks a person, who sees the batch and answers; use approval_status with the id you get.",
                Schema(new() { ["model"] = Text("The model's name."), ["changes"] = new JsonObject { ["type"] = "array", ["description"] = "The changes, as DbDataStore's changes route takes them: {action: insert|update|delete, entity, key, values, expected_version, effective_from}." }, ["description"] = Text("Why, in a sentence.") }, "model", "changes"),
                true, async call => Body(await store.ChangesAsync(call.Caller, Argument(call, "model"), System.Text.Encoding.UTF8.GetBytes(new JsonObject { ["changes"] = call.Arguments["changes"]?.DeepClone(), ["description"] = call.Arguments["description"]?.DeepClone() }.ToJsonString()), mayWrite: null, call.CancellationToken)),
                Summary: arguments => $"Write {(arguments["changes"] as JsonArray)?.Count ?? 0} change(s) to the master data model '{arguments["model"]}'{(arguments["description"] is { } why ? ": " + why : "")}.",
                Check: (caller, _) => caller.Holds(ServerRole.Author) || caller.Holds(ServerRole.Admin) ? null : "Writing master data needs the author role on this server."),
        ];
    }

    private static string Argument(McpCall call, string name) =>
        call.Arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : throw new McpArgumentException($"The argument '{name}' is needed.");

    private static JsonNode Body(StoreAnswer answer) =>
        answer.Status is >= 200 and < 300 ? answer.Body : throw new McpArgumentException(answer.Body is JsonObject o && o["error"] is { } e ? e.ToString() : $"The answer was {answer.Status}.");
}

/// <summary>A call whose arguments or whose rights do not allow it: the tool answers with the message and nothing ran.</summary>
public sealed class McpArgumentException(string message) : Exception(message);

/// <summary>
/// The MCP protocol of the module (plan 20 O12), over streamable HTTP in its plain form: each JSON-RPC message is a POST, each answer one JSON document (no stream is opened, no session is kept, so any instance of a group answers any call).
/// <c>initialize</c>, <c>ping</c>, <c>tools/list</c> and <c>tools/call</c> are answered; a notification is accepted and answers nothing.
/// </summary>
public sealed class McpServer(IReadOnlyList<McpTool> tools, McpApprovals approvals, string version)
{
    public static readonly string[] ProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly IReadOnlyDictionary<string, McpTool> _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    private readonly McpTool _status = Status(approvals);

    private static McpTool Status(McpApprovals approvals) => new("approval_status", "Where a request for a person's approval stands: waiting, approved (with what the run gave), denied or expired. Ask with the id you were given.",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["id"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("id") }, false,
        call => Task.FromResult<JsonNode>(Guid.TryParse(call.Arguments["id"]?.ToString(), out var id) ? approvals.StatusFor(call.Caller, id) : throw new McpArgumentException("The argument 'id' is the id of a request.")));

    /// <summary>The answer to one message, or null for a notification.</summary>
    public async Task<JsonNode?> HandleAsync(StoreCaller caller, JsonNode message, CancellationToken cancellationToken)
    {
        if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method) return Error(null, -32600, "Not a JSON-RPC request.");
        var id = request["id"]?.DeepClone();
        if (id is null) return null;
        try
        {
            switch (method)
            {
                case "initialize":
                    var asked = (request["params"] as JsonObject)?["protocolVersion"]?.ToString();
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "dbdatagateway", ["version"] = version },
                        ["instructions"] = "Reads run at once as you. A tool that changes something does not run: it asks a person and answers 'pending approval <id>'; ask approval_status with that id later.",
                    });
                case "ping": return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = new JsonArray([.. _tools.Values.Append(_status).Select(Describe)]) });
                case "tools/call":
                    return Result(id, await CallAsync(caller, request["params"] as JsonObject, cancellationToken));
                default: return Error(id, -32601, $"The method '{method}' is not offered.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(id, -32603, ex.Message);
        }
    }

    private static JsonNode Describe(McpTool tool) => new JsonObject
    {
        ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = tool.InputSchema.DeepClone(),
        ["annotations"] = new JsonObject { ["readOnlyHint"] = !tool.Effect, ["destructiveHint"] = tool.Effect, ["idempotentHint"] = false },
    };

    private async Task<JsonObject> CallAsync(StoreCaller caller, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.ToString() ?? "";
        var tool = name == _status.Name ? _status : _tools.GetValueOrDefault(name);
        if (tool is null) return Reply($"There is no tool '{name}'.", isError: true);
        var arguments = parameters?["arguments"] as JsonObject ?? [];
        try
        {
            if (tool.Effect)
            {
                if (tool.Check?.Invoke(caller, arguments) is { } refusal) return Reply(refusal, isError: true);
                var id = approvals.Request(caller, tool, arguments);
                return Reply($"pending approval {id:D}", isError: false, new JsonObject { ["status"] = "pending", ["approval"] = id.ToString("D"), ["expires_in_minutes"] = (int)approvals.Window.TotalMinutes });
            }
            if (tool.Check?.Invoke(caller, arguments) is { } denied) return Reply(denied, isError: true);
            var result = await tool.Run(new McpCall(caller, arguments, cancellationToken));
            return Reply(result.ToJsonString(), isError: false, result as JsonObject);
        }
        catch (Exception ex) when (ex is McpArgumentException or ServingException)
        {
            return Reply(ex.Message, isError: true);
        }
    }

    private static JsonObject Reply(string text, bool isError, JsonObject? structured = null)
    {
        var node = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = isError };
        if (structured is not null) node["structuredContent"] = structured.DeepClone();
        return node;
    }

    private static JsonObject Result(JsonNode id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
