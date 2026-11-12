using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>What the host's check or making of a draft says: whether it stands and, in words the model can act on, why not or what was done.</summary>
public sealed record ViewDraftOutcome(bool Ok, string Message);

/// <summary>
/// What the tools of <see cref="ViewDraftTools"/> need from the host (plan 32 section 7, DBR20): the library that knows the dashboard and report documents (<c>DbDataKit.Views</c>, with <c>UserViewDraftService</c>) is the host's to reference, as for
/// <see cref="AnswerViewTool"/>. The methods with <c>commit</c> only check when it is false (the runtime runs that before it asks the person) and store as the person when it is true.
/// </summary>
public interface IViewDraftHost
{
    ViewDraftOutcome Validate(string? kind, JsonObject document);

    ViewDraftOutcome Draft(string kind, string name, JsonObject document, bool commit);

    ViewDraftOutcome AddLayout(string kind, string name, JsonObject layout, bool commit);

    ViewDraftOutcome Pin(string dashboard, string widgetId, JsonObject widget, bool commit);
}

/// <summary>
/// The tools an agent builds dashboards and reports with (plan 32 section 7, DBR20): <c>views_validate</c>, <c>dashboard_draft</c>, <c>report_draft</c>, <c>layout_add</c> and <c>view_pin</c>. The agent writes a <b>document as data</b> (never code); the host's strict
/// loaders and catalog check judge it exactly as they judge a person's file and a refusal comes back with the path and line, so the draft is corrected in the loop. The four that store are <b>writes of definitions that ask the person every time</b> (a draft is saved as the
/// person's own user document by their approval) and check what they were given <b>before</b> asking (<see cref="AgentTool.Precheck"/>), so a draft that would be refused is never put to the person. A denied approval stores nothing.
/// </summary>
public static class ViewDraftTools
{
    public const string Validate = "views_validate";
    public const string DashboardDraft = "dashboard_draft";
    public const string ReportDraft = "report_draft";
    public const string LayoutAdd = "layout_add";
    public const string Pin = "view_pin";

    private const string NamePattern = "^[a-z][a-z0-9_-]{0,63}$";

    public static IReadOnlyList<AgentTool> Create(IViewDraftHost host) =>
    [
        ValidateTool(host),
        DraftTool(host, "dashboard", DashboardDraft),
        DraftTool(host, "report", ReportDraft),
        LayoutTool(host),
        PinTool(host),
    ];

    private static JsonObject Object(string description) => new() { ["type"] = "object", ["description"] = description };

    private static JsonObject Name(string description) => new() { ["type"] = "string", ["pattern"] = NamePattern, ["description"] = description };

    private static JsonObject Schema(JsonObject properties, params string[] required) =>
        new() { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()), ["properties"] = properties };

    private static ToolOutcome Outcome(ViewDraftOutcome o) => new(o.Message, !o.Ok);

    private static ToolOutcome? Refusal(ViewDraftOutcome o) => o.Ok ? null : Outcome(o);

    private static AgentTool ValidateTool(IViewDraftHost host) => new(Validate,
        "Check a dashboard or report document you wrote (`dashboard/1` or `report/1`: `schemaVersion`, `kind`, `title`, `widgets`, `layouts`) without storing it. The same checks a person's file gets run; every problem comes back with its code, its path and its line. Check, fix, check again, then draft.",
        Traits.ReadsDefinitions, Schema(new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("dashboard", "report"), ["description"] = "The kind you mean it to be; leave out to accept either." },
            ["document"] = Object("The document as JSON."),
        }, "document"), "views validate", (arguments, _) => Task.FromResult(Outcome(host.Validate(arguments["kind"]?.GetValue<string>(), (JsonObject)arguments["document"]!))))
    { DeclaredPrompting = Prompting.DoNotAsk };

    private static AgentTool DraftTool(IViewDraftHost host, string kind, string tool) => new(tool,
        $"Save a {kind} you wrote as a draft of the person's own: a `{kind}/1` document as data (widgets with `encodings` naming the fields, their layouts; no URLs, no code, no raw HTML). Check it with {Validate} first. The person is asked before it is saved; a draft is only ever theirs and is published through the publishing of the suite.",
        Traits.WritesDefinitions, Schema(new JsonObject { ["name"] = Name($"The {kind}'s name: lower case letters, digits, '_' and '-'."), ["document"] = Object($"The {kind} as JSON.") }, "name", "document"),
        $"views {kind} draft", (arguments, _) => Task.FromResult(Outcome(host.Draft(kind, arguments["name"]!.GetValue<string>(), (JsonObject)arguments["document"]!, true))))
    {
        DeclaredPrompting = Prompting.Ask,
        Precheck = arguments => Refusal(host.Draft(kind, arguments["name"]!.GetValue<string>(), (JsonObject)arguments["document"]!, false)),
    };

    private static AgentTool LayoutTool(IViewDraftHost host) => new(LayoutAdd,
        "Add a layout to a dashboard or report of the person's that exists: a print layout for a dashboard (`type: flow`), a phone canvas for a report. It is never the default. The person is asked before it is saved.",
        Traits.WritesDefinitions, Schema(new JsonObject
        {
            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("dashboard", "report") },
            ["name"] = Name("The document's name."),
            ["layout"] = Object("The layout as JSON: `name`, `type`, `when`, and its tiles, canvas or sections."),
        }, "kind", "name", "layout"), "views layout add", (arguments, _) => Task.FromResult(Outcome(host.AddLayout(arguments["kind"]!.GetValue<string>(), arguments["name"]!.GetValue<string>(), (JsonObject)arguments["layout"]!, true))))
    {
        DeclaredPrompting = Prompting.Ask,
        Precheck = arguments => Refusal(host.AddLayout(arguments["kind"]!.GetValue<string>(), arguments["name"]!.GetValue<string>(), (JsonObject)arguments["layout"]!, false)),
    };

    private static AgentTool PinTool(IViewDraftHost host) => new(Pin,
        "Pin one widget to a dashboard of the person's: the widget is added and placed in the default layout below what is there. The person is asked before it is saved.",
        Traits.WritesDefinitions, Schema(new JsonObject
        {
            ["dashboard"] = Name("The dashboard's name."),
            ["widget_id"] = Name("The id the widget gets in the dashboard."),
            ["widget"] = Object("The widget as JSON, as a dashboard writes it."),
        }, "dashboard", "widget_id", "widget"), "views pin", (arguments, _) => Task.FromResult(Outcome(host.Pin(arguments["dashboard"]!.GetValue<string>(), arguments["widget_id"]!.GetValue<string>(), (JsonObject)arguments["widget"]!, true))))
    {
        DeclaredPrompting = Prompting.Ask,
        Precheck = arguments => Refusal(host.Pin(arguments["dashboard"]!.GetValue<string>(), arguments["widget_id"]!.GetValue<string>(), (JsonObject)arguments["widget"]!, false)),
    };
}
