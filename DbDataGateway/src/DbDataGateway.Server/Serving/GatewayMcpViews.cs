using System.Text.Json.Nodes;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataKit.Views;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The tools an agent builds dashboards and reports with, through the <c>mcp</c> module (plan 32 DBR20): <c>views_validate</c> reads (it stores nothing, so it runs at once); <c>dashboard_draft</c>, <c>report_draft</c>, <c>layout_add</c> and <c>view_pin</c> are
/// <b>writes: a draft is the caller's own user document and is saved only when a person approves the request</b> (the module's approvals, as <c>write_entity_changes</c>). Every one runs the library's check (<see cref="AgentViewDrafts"/>: the same strict loaders and the
/// catalog check a person's file gets, against the models the caller may read, with no explicit <c>sql</c>) and a draft that would be refused is refused <b>when it is asked for</b>, so no person is asked to approve it. A document saved is a version of a user document
/// of the caller (<see cref="UserViewStore"/>): when the caller is a service account, it is the service account's, to share or promote as any other.
/// </summary>
public static class McpViewTools
{
    public static IReadOnlyList<McpTool> Create(Func<StoreCaller, GatewayCaller?> callerOf, SemanticModels semantic, UserViewStore store)
    {
        UserViewDraftService? ServiceOf(StoreCaller caller)
        {
            if (callerOf(caller) is not { } gateway) return null;
            // the models the caller may read are the ones a draft may name; a model that is not theirs is "not bound", as it would be at publish
            var catalogs = semantic.All.Where(m => m.Sources.Values.All(gateway.Principal.CanUse))
                .ToDictionary(m => m.Name, m => new ModelCatalog(m.Name, m.Version ?? 0, m.Catalog).Binding, StringComparer.Ordinal);
            return new UserViewDraftService(store, gateway.Id, gateway.Name ?? caller.UserId, $"{gateway.Name ?? caller.UserId} (agent)", new DraftOptions(AllowSql: false, Catalogs: catalogs));
        }

        string? Refusal(StoreCaller caller, Func<UserViewDraftService, DraftResult> check)
        {
            if (!caller.Holds(ServerRole.Viewer)) return "Drafting a dashboard or a report needs the viewer role on this server.";
            if (ServiceOf(caller) is not { } service) return "Documents are for the people and service accounts of the identity home.";
            var result = check(service);
            return result.Ok ? null : result.Message;
        }

        JsonNode Run(McpCall call, Func<UserViewDraftService, DraftResult> act)
        {
            var service = ServiceOf(call.Caller) ?? throw new McpArgumentException("Documents are for the people and service accounts of the identity home.");
            var result = act(service);
            return result.Ok ? new JsonObject { ["valid"] = true, ["message"] = result.Message } : throw new McpArgumentException(result.Message);
        }

        static string Text(JsonObject arguments, string name) =>
            arguments[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : throw new McpArgumentException($"The argument '{name}' is needed.");

        static JsonObject Document(JsonObject arguments, string name) =>
            arguments[name] as JsonObject ?? throw new McpArgumentException($"The argument '{name}' is needed, as a JSON object.");

        static JsonObject Schema(JsonObject properties, params string[] required) => new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray([.. required.Select(r => (JsonNode)JsonValue.Create(r)!)]) };
        static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
        static JsonObject Obj(string description) => new() { ["type"] = "object", ["description"] = description };
        static JsonObject Kind(string description) => new() { ["type"] = "string", ["enum"] = new JsonArray("dashboard", "report"), ["description"] = description };

        McpTool Draft(string kind) => new($"{kind}_draft",
            $"Saves a {kind} you wrote as a draft of your own (a `{kind}/1` document as data: widgets whose `encodings` name the fields of a model, and their layouts; no URLs, no code, no raw HTML, no sql). It does NOT save now: it is checked at once, and if it is valid it asks a person, who sees it and answers; use approval_status with the id you get. Check it with views_validate first.",
            Schema(new() { ["name"] = Str($"The {kind}'s name: lower case letters, digits, '_' and '-'."), ["document"] = Obj($"The {kind} as JSON.") }, "name", "document"), true,
            call => Task.FromResult(Run(call, s => s.Draft(kind, Text(call.Arguments, "name"), Document(call.Arguments, "document"), true))),
            Summary: arguments => $"Save a {kind} '{arguments["name"]}' ('{arguments["document"]?["title"]}') as a draft of the one who asked.",
            Check: (caller, arguments) => Refusal(caller, s => s.Draft(kind, Text(arguments, "name"), Document(arguments, "document"), false)));

        return
        [
            new("views_validate", "Checks a dashboard or report document you wrote (`dashboard/1` or `report/1`) without saving it, as a person's file is checked, against the semantic models you may read. Every problem comes back with its code, path and line.",
                Schema(new() { ["kind"] = Kind("The kind you mean it to be; leave out to accept either."), ["document"] = Obj("The document as JSON.") }, "document"), false,
                call => Task.FromResult(Run(call, s => s.Validate(call.Arguments["kind"]?.GetValue<string>(), Document(call.Arguments, "document")))),
                Check: (caller, _) => caller.Holds(ServerRole.Viewer) ? null : "Checking a document needs the viewer role on this server."),
            Draft("dashboard"),
            Draft("report"),
            new("layout_add", "Adds a layout to a dashboard or report of yours (a print layout for a dashboard, a phone canvas for a report). It is never the default. It asks a person, as the drafts do; use approval_status with the id you get.",
                Schema(new() { ["kind"] = Kind("The document's kind."), ["name"] = Str("The document's name."), ["layout"] = Obj("The layout as JSON.") }, "kind", "name", "layout"), true,
                call => Task.FromResult(Run(call, s => s.AddLayout(Text(call.Arguments, "kind"), Text(call.Arguments, "name"), Document(call.Arguments, "layout"), true))),
                Summary: arguments => $"Add the layout '{arguments["layout"]?["name"]}' to the {arguments["kind"]} '{arguments["name"]}'.",
                Check: (caller, arguments) => Refusal(caller, s => s.AddLayout(Text(arguments, "kind"), Text(arguments, "name"), Document(arguments, "layout"), false))),
            new("view_pin", "Pins one widget to a dashboard of yours: it is added and placed in the default layout below what is there. It asks a person, as the drafts do; use approval_status with the id you get.",
                Schema(new() { ["dashboard"] = Str("The dashboard's name."), ["widget_id"] = Str("The id the widget gets."), ["widget"] = Obj("The widget as JSON, as a dashboard writes it.") }, "dashboard", "widget_id", "widget"), true,
                call => Task.FromResult(Run(call, s => s.Pin(Text(call.Arguments, "dashboard"), Text(call.Arguments, "widget_id"), Document(call.Arguments, "widget"), true))),
                Summary: arguments => $"Pin the widget '{arguments["widget_id"]}' to the dashboard '{arguments["dashboard"]}'.",
                Check: (caller, arguments) => Refusal(caller, s => s.Pin(Text(arguments, "dashboard"), Text(arguments, "widget_id"), Document(arguments, "widget"), false))),
        ];
    }
}
