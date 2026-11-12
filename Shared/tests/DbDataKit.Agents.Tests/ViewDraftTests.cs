using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>
/// Plan 32 DBR20: the tools an agent builds dashboards and reports with, over the same strict loaders and catalog check a person's file goes through, in scripted conversations with the fake model (plan 25 section 2): a draft is checked, corrected on a
/// diagnostic and saved only by the person's approval; what a human's file is refused for is refused for an agent; a denied approval stores nothing.
/// </summary>
public sealed class ViewDraftTests : IDisposable
{
    private static readonly AgentRunContext Context = new("alice", "dashboard-helper", "1");

    private readonly StateEngines engines = new();

    public void Dispose() => engines.Dispose();

    // the model "sales" as the catalog check sees it: the engine's validator is the host's adapter; this one knows the members and says unknown_member as the engine does
    private static readonly string[] Measures = ["orders.net_revenue"];
    private static readonly string[] Attributes = ["customers.region"];

    private sealed class Members : IRequestValidator
    {
        public IReadOnlyList<RequestProblem> Validate(JsonObject request)
        {
            var problems = new List<RequestProblem>();
            foreach (var (list, known) in new[] { ("measures", Measures), ("attributes", Attributes) })
                if (request["query"]?[list] is JsonArray names)
                    for (var i = 0; i < names.Count; i++)
                        if (!known.Contains(names[i]!.GetValue<string>()))
                            problems.Add(new RequestProblem("unknown_member", $"$.query.{list}[{i}]", $"`{names[i]}` is not a member of the model."));
            return problems;
        }
    }

    private static readonly IReadOnlyDictionary<string, CatalogBinding> Catalogs = new Dictionary<string, CatalogBinding> { ["sales"] = new(new CatalogRoles("sales", Measures, Attributes), new Members()) };

    private sealed class Host(UserViewDraftService service) : IViewDraftHost
    {
        public ViewDraftOutcome Validate(string? kind, JsonObject document) => Map(service.Validate(kind, document));

        public ViewDraftOutcome Draft(string kind, string name, JsonObject document, bool commit) => Map(service.Draft(kind, name, document, commit));

        public ViewDraftOutcome AddLayout(string kind, string name, JsonObject layout, bool commit) => Map(service.AddLayout(kind, name, layout, commit));

        public ViewDraftOutcome Pin(string dashboard, string widgetId, JsonObject widget, bool commit) => Map(service.Pin(dashboard, widgetId, widget, commit));

        private static ViewDraftOutcome Map(DraftResult r) => new(r.Ok, r.Message);
    }

    private (UserViewStore Store, IReadOnlyList<AgentTool> Tools) Open(bool allowSql = false)
    {
        var database = engines.Provision(StateEngineIds.Sqlite).Open([UserViewsModule.Module], new StateScope(Group: "main"));
        var store = new UserViewStore(database);
        var service = new UserViewDraftService(store, "alice", "Alice", "alice (agent dashboard-helper)", new DraftOptions(allowSql, Catalogs));
        return (store, ViewDraftTools.Create(new Host(service)));
    }

    private static JsonObject Golden() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "views", "agent", "revenue-dashboard.json")))!;

    private static JsonObject Clone(JsonNode node) => (JsonObject)node.DeepClone();

    private static async Task<AgentRunResult> Run(ScriptedChatClient client, IReadOnlyList<AgentTool> tools, ScriptedApprover approver) =>
        await new AgentRunner(client, tools).RunAsync([new ChatMessage(ChatRole.User, "draw revenue by region")], Context, new AgentRunOptions { Gate = new ApprovalGate(approver, PromptingPolicy.None) });

    [Fact]
    public void The_five_tools_are_named_and_only_the_checking_one_runs_without_asking()
    {
        var (_, tools) = Open();
        Assert.Equal(["views_validate", "dashboard_draft", "report_draft", "layout_add", "view_pin"], tools.Select(t => t.Name));
        Assert.Equal(Prompting.DoNotAsk, PromptingResolver.Resolve(tools[0], PromptingPolicy.None, null));
        Assert.All(tools.Skip(1), t => Assert.Equal(Prompting.Ask, PromptingResolver.Resolve(t, PromptingPolicy.None, null)));
        Assert.All(tools.Skip(1), t => Assert.NotNull(t.Precheck));
    }

    [Fact]
    public async Task A_scripted_conversation_drafts_from_a_description_gets_a_diagnostic_fixes_it_and_the_approved_draft_equals_the_golden()
    {
        var (store, tools) = Open();
        var bad = Clone(Golden());
        bad["widgets"]!["by_region"]!["encodings"]!["x"] = "customers.country";   // an encoded field that is not in the model
        var client = new ScriptedChatClient(
            ScriptStep.Call("views_validate", new JsonObject { ["kind"] = "dashboard", ["document"] = bad }),
            ScriptStep.Call("views_validate", new JsonObject { ["kind"] = "dashboard", ["document"] = Golden() }),
            ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = Golden() }),
            ScriptStep.Say("The dashboard `revenue` is saved as a draft of yours."));
        var approver = new ScriptedApprover(true);

        var run = await Run(client, tools, approver);

        Assert.Equal(AgentStop.Completed, run.Stop);
        var diagnostic = Transcript.ResultTexts(client.Requests[1]).Single();
        Assert.Contains("error=\"true\"", diagnostic);
        Assert.Contains("[unknown_member]", diagnostic);   // the engine's own code
        Assert.Contains("customers.country", diagnostic);
        Assert.Contains("widgets.by_region", diagnostic);
        Assert.Contains("(line ", diagnostic);
        Assert.Contains("is valid: 2 widgets, 2 layouts", Transcript.ResultTexts(client.Requests[2]).Last());
        var saved = Assert.IsType<UserView>(store.Get("alice", "dashboard", "revenue"));
        Assert.Equal(1, saved.Version);
        Assert.True(JsonNode.DeepEquals(Golden(), JsonNode.Parse(saved.Text)), saved.Text);
        var asked = Assert.Single(approver.Asked);
        Assert.Equal("dashboard_draft", asked.Tool);
        Assert.StartsWith("user: draw revenue by region\nassistant calls views_validate", Transcript.Render(run.Messages));
    }

    [Fact]
    public async Task A_denied_approval_stores_nothing_and_the_model_is_told()
    {
        var (store, tools) = Open();
        var client = new ScriptedChatClient(
            ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = Golden() }),
            ScriptStep.Say("Understood, nothing was saved."));

        var run = await Run(client, tools, new ScriptedApprover(false));

        Assert.Null(store.Get("alice", "dashboard", "revenue"));
        Assert.Empty(store.List(new UserViewViewer("alice", "Alice", []), null));
        Assert.Contains("error=\"true\"", Transcript.ResultTexts(client.Requests[1]).Single());
        Assert.Equal(AgentStop.Completed, run.Stop);
    }

    [Fact]
    public async Task A_draft_that_would_be_refused_is_refused_before_the_person_is_asked()
    {
        var (store, tools) = Open();
        var bad = Clone(Golden());
        bad["widgets"]!["by_region"]!["encodings"]!["x"] = "customers.country";
        var client = new ScriptedChatClient(
            ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = bad }),
            ScriptStep.Say("I will fix it."));
        var approver = new ScriptedApprover();   // asked once would throw

        await Run(client, tools, approver);

        Assert.Empty(approver.Asked);
        Assert.Null(store.Get("alice", "dashboard", "revenue"));
        var text = Transcript.ResultTexts(client.Requests[1]).Single();
        Assert.Contains("nothing was stored", text);
        Assert.Contains("customers.country", text);
    }

    [Theory]
    [InlineData("image-url", "DBD-VIEW-003")]
    [InlineData("raw-html", "DBD-VIEW-002")]
    [InlineData("vega-expression", "DBD-VIEW-004")]
    [InlineData("aggregate", "DBD-VIEW-004")]
    [InlineData("sql", "DBD-VIEW-007")]
    public async Task What_a_human_s_file_is_refused_for_is_refused_for_an_agent_by_the_same_validator(string case_, string code)
    {
        var (store, tools) = Open();
        var document = Clone(Golden());
        var widgets = (JsonObject)document["widgets"]!;
        switch (case_)
        {
            case "image-url": widgets["logo"] = JsonNode.Parse("""{"type":"image","hash":"https://evil.example/logo.png","media_type":"image/png","alt":"logo"}"""); break;
            case "raw-html": widgets["note"] = JsonNode.Parse("""{"type":"text","text":"<script>alert(1)</script>"}"""); break;
            case "vega-expression": widgets["by_region"]!["encodings"]!["y"] = JsonNode.Parse("""{"field":"orders.net_revenue","type":"quantitative","condition":{"test":"datum.x > 1"}}"""); break;
            case "aggregate": widgets["by_region"]!["encodings"]!["y"] = JsonNode.Parse("""{"field":"orders.net_revenue","aggregate":"sum"}"""); break;
            case "sql": widgets["revenue"]!["encodings"]!["value"] = "revenue"; widgets["revenue"]!["query"] = JsonNode.Parse("""{"mode":"explicit","semantic":{"model":"sales","sql":"select sum(orders.net_revenue) as revenue from sales"}}"""); break;
        }
        var client = new ScriptedChatClient(ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = document }), ScriptStep.Say("ok"));

        await Run(client, tools, new ScriptedApprover());

        var result = Transcript.ResultTexts(client.Requests[1]).Single();
        Assert.Contains("error=\"true\"", result);
        Assert.True(result.Contains(code), result);
        Assert.Null(store.Get("alice", "dashboard", "revenue"));
    }

    [Fact]
    public async Task An_explicit_sql_is_allowed_where_the_author_may_use_it()
    {
        var (store, tools) = Open(allowSql: true);
        var document = Clone(Golden());
        document["widgets"]!["revenue"]!["encodings"]!["value"] = "revenue";
        document["widgets"]!["revenue"]!["query"] = JsonNode.Parse("""{"mode":"explicit","semantic":{"model":"sales","sql":"select sum(orders.net_revenue) as revenue from sales"}}""");
        var client = new ScriptedChatClient(ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = document }), ScriptStep.Say("ok"));

        await Run(client, tools, new ScriptedApprover(true));

        Assert.NotNull(store.Get("alice", "dashboard", "revenue"));
    }

    [Fact]
    public async Task A_report_is_drafted_a_layout_added_and_a_widget_pinned_each_as_a_new_version_by_approval()
    {
        var (store, tools) = Open();
        var report = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "views", "good", "report-minimal.json")))!;
        var phone = JsonNode.Parse("""{"name":"phone","type":"canvas","when":{"width":{"max":600}},"canvas":{"width":390,"height":900},"tiles":[{"widget":"note","x":10,"y":10,"w":370,"h":100}]}""")!;
        var pin = JsonNode.Parse("""{"type":"metric","label":"Revenue","encodings":{"value":"orders.net_revenue"},"query":{"model":"sales"}}""")!;
        var client = new ScriptedChatClient(
            ScriptStep.Call("report_draft", new JsonObject { ["name"] = "minimal", ["document"] = report }),
            ScriptStep.Call("layout_add", new JsonObject { ["kind"] = "report", ["name"] = "minimal", ["layout"] = phone }),
            ScriptStep.Call("dashboard_draft", new JsonObject { ["name"] = "revenue", ["document"] = Golden() }),
            ScriptStep.Call("view_pin", new JsonObject { ["dashboard"] = "revenue", ["widget_id"] = "total", ["widget"] = pin }),
            ScriptStep.Say("done"));
        var approver = new ScriptedApprover(true, true, true, true);

        var run = await Run(client, tools, approver);

        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.Equal(["report_draft", "layout_add", "dashboard_draft", "view_pin"], approver.Asked.Select(a => a.Tool));
        var minimal = store.Get("alice", "report", "minimal")!;
        Assert.Equal(2, minimal.Version);
        Assert.Equal(["print", "phone"], JsonNode.Parse(minimal.Text)!["layouts"]!.AsArray().Select(l => l!["name"]!.GetValue<string>()));
        var revenue = store.Get("alice", "dashboard", "revenue")!;
        Assert.Equal(2, revenue.Version);
        var doc = JsonNode.Parse(revenue.Text)!;
        Assert.NotNull(doc["widgets"]!["total"]);
        Assert.Contains(doc["layouts"]![0]!["tiles"]!.AsArray(), t => t!["widget"]!.GetValue<string>() == "total");
        Assert.True(ViewsLoader.Load(revenue.Text, "x.json").Ok);
    }

    [Fact]
    public void A_layout_that_claims_to_be_the_default_a_missing_document_and_a_taken_widget_id_are_refused_in_words()
    {
        var (store, tools) = Open();
        var host = new Host(new UserViewDraftService(store, "alice", "Alice", "alice", new DraftOptions(false, Catalogs)));
        Assert.Contains("no dashboard `nope`", host.Pin("nope", "w", new JsonObject(), false).Message);
        Assert.Contains("draft it first", host.AddLayout("dashboard", "nope", new JsonObject(), false).Message);
        Assert.True(host.Draft("dashboard", "revenue", Golden(), true).Ok);
        Assert.Contains("not the default", host.AddLayout("dashboard", "revenue", (JsonObject)JsonNode.Parse("""{"name":"x","type":"flow","default":true,"sections":[]}""")!, false).Message);
        Assert.Contains("already has a widget `revenue`", host.Pin("revenue", "revenue", new JsonObject(), false).Message);
        Assert.Contains("already have a dashboard", host.Draft("dashboard", "revenue", Golden(), true).Message);
        Assert.Contains("is not a name", host.Draft("dashboard", "Bad Name", Golden(), false).Message);
        Assert.Contains("DBD-VIEW-001", host.Draft("report", "r", Golden(), false).Message);
        Assert.Equal(1, store.Get("alice", "dashboard", "revenue")!.Version);
    }
}
