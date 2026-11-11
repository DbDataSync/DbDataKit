using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The agents of the console (plan 27a AG14 to AG17, AG29 to AG32) and its knowledge screens (KN28, KN36 to KN38) through the API the screens call: definitions with what each tool asks, the model's space and skills; the install policy with a confirmed
/// loosening; a conversation with a scripted model whose step waits for the person; the inbox; the record; the console's half of an outside agent's approvals; and the knowledge overview, worklists and previews. No model, no network: a scripted client.
/// </summary>
public sealed class ConsoleAgentsTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-agents-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private readonly FakeModels models = new();
    private string admin = "";
    private string other = "";
    private string viewer = "";

    private sealed class FakeModels : IAgentModels
    {
        public ScriptedChatClient? Client { get; set; }

        public BoundModel? For(string slot, string? channel) => Client is { } c ? new BoundModel(c, "scripted", new ModelPrice(1m, 2m), 10_000) : null;
    }

    private sealed class DemoTools : IAgentToolSource
    {
        public NounRouter Router() => new("demo",
        [
            new Noun("thing", "Things",
            [
                new Verb("show", "Show a thing", EffectClass.OfflineOnly, "<name>", async (a, c) => { c.Data["name"] = a.FirstOrDefault(); await c.Out.WriteLineAsync("a widget"); return 0; }, DataSchemas.Object([("name", DataSchemas.String)])),
            ]),
        ], "1");

        public McpOptions Options { get; } = new(new HashSet<string>());
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var agent = Path.Combine(directory, "agents", "helper");
        Directory.CreateDirectory(Path.Combine(agent, "instructions"));
        File.WriteAllText(Path.Combine(agent, "agent.yml"), "agent: 1\nname: helper\ntitle: Helper\naudience: back-end\nmodel: { slot: default }\ninstructions: [instructions/role.md]\nskills: [demo-skill]\ntools:\n  - command: thing show\n    prompting: ask\n");
        File.WriteAllText(Path.Combine(agent, "instructions", "role.md"), "You help with things. Be brief.\n");
        Directory.CreateDirectory(Path.Combine(directory, "skills", "demo-skill"));
        File.WriteAllText(Path.Combine(directory, "skills", "demo-skill", "SKILL.md"), "---\nname: demo-skill\ndescription: Shows things\n---\nUse thing show first.\n");

        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services =>
        {
            services.AddSingleton<IAgentModels>(models);
            services.AddSingleton<IAgentToolSource, DemoTools>();
        });
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        other = SignIn("Olga", UserRole.Admin);
        viewer = SignIn("Vic", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path, string? session) => Call(HttpMethod.Get, path, session);

    // ---- definitions: AG16 in the console's folder, AG30, AG31, AG32 ----

    [Fact]
    public async Task An_agent_is_listed_and_its_detail_says_what_each_tool_asks_the_models_space_and_its_skills()
    {
        var (_, list) = await Get("/api/agents/definitions", viewer);
        var listing = Assert.Single(list!.AsArray())!;
        Assert.Equal(("helper", "back-end", "default", 0, false), ((string)listing["name"]!, (string)listing["audience"]!, (string)listing["modelSlot"]!, (int)listing["problems"]!, (bool)listing["modelBound"]!));

        var (status, detail) = await Get("/api/agents/definitions/helper", viewer);

        Assert.Equal(HttpStatusCode.OK, status);
        var tool = Assert.Single(detail!["prompting"]!.AsArray())!;
        Assert.Equal(("thing_show", "Ask", "agent"), ((string)tool["tool"]!, (string)tool["effective"]!, (string)tool["layer"]!));
        Assert.True((int)detail["space"]!["totalTokens"]! > 0);
        Assert.Contains(detail["space"]!["groups"]!.AsArray(), g => (string)g!["name"]! == "instructions");
        var skill = Assert.Single(detail["skills"]!.AsArray())!;
        Assert.Equal(("demo-skill", "Shows things", true), ((string)skill["name"]!, (string)skill["description"]!, (bool)skill["fits"]!));
        Assert.Contains("Use thing show first.", (string)skill["exportFiles"]![".claude/skills/demo-skill/SKILL.md"]!);
        Assert.Empty(detail["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task A_definition_with_a_problem_is_refused_with_the_line_and_the_file_stays_as_it_was()
    {
        var bad = "agent: 1\nname: helper\naudience: back-end\nmodel: { slot: default }\ntools:\n  - command: thing explode\n";

        var (check, checked_) = await Call(HttpMethod.Post, "/api/agents/definitions/helper/check", admin, new YamlBody(bad));
        var (save, saved) = await Call(HttpMethod.Put, "/api/agents/definitions/helper", admin, new YamlBody(bad));

        Assert.Equal(HttpStatusCode.OK, check);
        Assert.Equal(6, (int)checked_!["diagnostics"]![0]!["line"]!);
        Assert.Contains("explode", (string)checked_["diagnostics"]![0]!["message"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, save);
        Assert.Contains("thing show", await File.ReadAllTextAsync(Path.Combine(directory, "agents", "helper", "agent.yml")));
        Assert.NotNull(saved);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/agents/definitions/helper", viewer, new YamlBody(bad))).Status);
    }

    [Fact]
    public async Task A_setting_in_a_definition_below_the_policys_minimum_is_a_diagnostic_naming_the_layer()
    {
        await Call(HttpMethod.Put, "/api/agents/policy", admin, new YamlBody("agent-policy: 1\nprompting:\n  minimums:\n    reads-definitions: ask\n"));
        var yaml = "agent: 1\nname: helper\naudience: back-end\nmodel: { slot: default }\ntools:\n  - command: thing show\n    prompting: do-not-ask\n";

        var (_, checkedDetail) = await Call(HttpMethod.Post, "/api/agents/definitions/helper/check", admin, new YamlBody(yaml));

        Assert.Contains(checkedDetail!["diagnostics"]!.AsArray(), d => (string)d!["code"]! == "DBD-AGT-021");
        var row = checkedDetail["prompting"]!.AsArray()[0]!;
        Assert.Equal("Ask", (string)row["effective"]!);
        Assert.Contains("install policy", (string)row["warning"]!);
    }

    // ---- AG17: the install policy ----

    [Fact]
    public async Task Loosening_the_policy_needs_a_confirmation_that_names_the_effect_and_the_change_is_recorded_with_who_made_it()
    {
        var strict = "agent-policy: 1\nprompting:\n  minimums:\n    writes-data: ask\nlimits:\n  conversation:\n    turns: 10\n";
        var (first, _) = await Call(HttpMethod.Put, "/api/agents/policy", admin, new YamlBody(strict));
        Assert.Equal(HttpStatusCode.OK, first);   // from nothing: tightening needs no confirmation

        var loose = "agent-policy: 1\nprompting:\n  minimums:\n    writes-data: ask-once\nlimits:\n  conversation:\n    turns: 10\n";
        var (preview, previewBody) = await Call(HttpMethod.Post, "/api/agents/policy/preview", admin, new YamlBody(loose));
        var (refused, refusedBody) = await Call(HttpMethod.Put, "/api/agents/policy", admin, new YamlBody(loose));

        Assert.Equal(HttpStatusCode.OK, preview);
        Assert.True((bool)previewBody!["loosens"]!);
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("asks once per conversation", (string)refusedBody!["changes"]![0]!["text"]!);
        Assert.Contains("writes-data: ask\n", await File.ReadAllTextAsync(Path.Combine(directory, "agent-policy.yml")));   // not saved

        var (confirmed, _) = await Call(HttpMethod.Put, "/api/agents/policy", admin, new YamlBody(loose, Confirmed: true));

        Assert.Equal(HttpStatusCode.OK, confirmed);
        Assert.Contains("ask-once", await File.ReadAllTextAsync(Path.Combine(directory, "agent-policy.yml")));
        var (_, audit) = await Get("/api/agents/audit?kind=policy-loosened", admin);
        var row = Assert.Single(audit!.AsArray())!;
        Assert.Equal(("Ada", "install-policy"), ((string)row["personName"]!, (string)row["agent"]!));
        Assert.Contains("Lowers the minimum for writes-data", (string)row["detail"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/agents/policy", viewer, new YamlBody(strict))).Status);
    }

    [Fact]
    public async Task A_policy_that_does_not_read_is_refused_with_its_diagnostic()
    {
        var (status, body) = await Call(HttpMethod.Put, "/api/agents/policy", admin, new YamlBody("agent-policy: 1\nprompting:\n  minimums:\n    writes-dta: ask\n"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("writes-data", (string)body!["diagnostics"]![0]!["message"]!);
    }

    // ---- AG14, AG15: a conversation ----

    [Fact]
    public async Task With_no_model_bound_a_conversation_does_not_start_and_says_how_it_is_bound()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("No model is bound to the slot 'default'", (string)body!["error"]!);
    }

    [Fact]
    public async Task A_step_that_asks_waits_for_the_person_and_only_that_person_can_answer_it_and_the_record_names_them()
    {
        models.Client = new ScriptedChatClient(ScriptStep.Call("thing_show", new { args = new[] { "widget" } }).WithUsage(100, 10), ScriptStep.Say("It is a widget.").WithUsage(200, 20));
        var (started, conversation) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper", "A widget"));
        Assert.Equal(HttpStatusCode.OK, started);
        var id = (string)conversation!["id"]!;

        var (sent, _) = await Call(HttpMethod.Post, $"/api/agents/conversations/{id}/messages", admin, new SendMessageBody("what is widget?"));
        Assert.Equal(HttpStatusCode.Accepted, sent);
        JsonNode? pending = null;
        for (var i = 0; i < 100 && pending is null; i++)
        {
            var (_, inbox) = await Get("/api/agents/approvals", admin);
            pending = inbox!.AsArray().FirstOrDefault();
            if (pending is null) await Task.Delay(50);
        }
        Assert.NotNull(pending);
        Assert.Equal(("thing_show", "reads-definitions", "Ada"), ((string)pending["tool"]!, (string)pending["traits"]!, (string)pending["personName"]!));
        Assert.Contains("thing show", (string)pending["preview"]!);
        var approval = (string)pending["id"]!;

        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/agents/approvals/{approval}", other, new AnswerBody(true))).Status);   // another administrator is not the person
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/agents/approvals/{approval}", viewer, new AnswerBody(true))).Status);
        Assert.Empty((await Get("/api/agents/approvals", other)).Body!.AsArray());                                                                       // and it is not in their inbox

        var (answered, _) = await Call(HttpMethod.Post, $"/api/agents/approvals/{approval}", admin, new AnswerBody(true, "go ahead"));
        Assert.Equal(HttpStatusCode.OK, answered);
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(id);

        var (_, view) = await Get($"/api/agents/conversations/{id}", admin);
        Assert.Equal("open", (string)view!["conversation"]!["status"]!);
        Assert.Equal(["user", "assistant"], view["text"]!.AsArray().Select(t => (string)t!["role"]!));
        Assert.Equal(2, view["turns"]!.AsArray().Count);
        Assert.Equal(100, (int)view["turns"]![0]!["inputTokens"]!);
        Assert.All(view["audit"]!.AsArray(), a => Assert.Equal(("Ada", "helper"), ((string)a!["personName"]!, (string)a["agent"]!)));
        var kinds = view["audit"]!.AsArray().Select(a => (string)a!["kind"]!).ToList();
        Assert.Contains("approval-asked", kinds);
        Assert.Contains("approval-approved", kinds);
        Assert.Contains("tool-called", kinds);
        Assert.Equal(("approved", "Ada", "go ahead"), ((string)view["approvals"]![0]!["status"]!, (string)view["approvals"]![0]!["answeredByName"]!, (string)view["approvals"]![0]!["note"]!));

        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/agents/conversations/{id}", viewer)).Status);   // a viewer sees only their own
        Assert.Equal(HttpStatusCode.OK, (await Get($"/api/agents/conversations/{id}", other)).Status);           // an administrator sees all
        var (_, usage) = await Get("/api/agents/usage", admin);
        Assert.Equal(2, (int)usage!["install"]!["turns"]!);
    }

    [Fact]
    public async Task A_denied_step_does_not_run_and_the_agent_says_so()
    {
        models.Client = new ScriptedChatClient(ScriptStep.Call("thing_show", new { args = new[] { "widget" } }), ScriptStep.Say("I could not look."));
        var (_, conversation) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper"));
        var id = (string)conversation!["id"]!;
        await Call(HttpMethod.Post, $"/api/agents/conversations/{id}/messages", admin, new SendMessageBody("look"));
        JsonNode? pending = null;
        for (var i = 0; i < 100 && pending is null; i++) { pending = (await Get("/api/agents/approvals", admin)).Body!.AsArray().FirstOrDefault(); if (pending is null) await Task.Delay(50); }

        await Call(HttpMethod.Post, $"/api/agents/approvals/{(string)pending!["id"]!}", admin, new AnswerBody(false, "not now"));
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(id);

        var (_, view) = await Get($"/api/agents/conversations/{id}", admin);
        var kinds = view!["audit"]!.AsArray().Select(a => (string)a!["kind"]!).ToList();
        Assert.Contains("approval-denied", kinds);
        Assert.DoesNotContain("tool-called", kinds);
        Assert.Equal("denied", (string)view["approvals"]![0]!["status"]!);
    }

    [Fact]
    public async Task A_viewer_cannot_start_a_conversation_and_a_message_to_someone_elses_is_refused()
    {
        models.Client = new ScriptedChatClient(ScriptStep.Say("hi"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/agents/conversations", viewer, new StartConversationBody("helper"))).Status);
        var (_, conversation) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper"));

        var (status, body) = await Call(HttpMethod.Post, $"/api/agents/conversations/{(string)conversation!["id"]!}/messages", other, new SendMessageBody("hello"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("no such conversation of yours", (string)body!["error"]!);
    }

    [Fact]
    public async Task Nobody_signed_in_reads_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/agents/definitions", null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/knowledge/overview", null)).Status);
    }

    // ---- AG29: an outside agent's approvals and the record ----

    [Fact]
    public async Task An_outside_agents_step_waits_in_the_inbox_and_the_answer_is_the_persons_own_and_ends_in_the_record()
    {
        var (_, asked) = await Call(HttpMethod.Post, "/api/agents/outside/ask", admin, new OutsideAskBody("claude-code", "2.1", "build_model_add", "command model add orders  [writes-definitions]", "{\"args\":[\"orders\"]}", "writes-definitions"));
        var id = (string)asked!["id"]!;
        Assert.Equal("pending", (string)(await Get($"/api/agents/outside/ask/{id}", admin)).Body!["status"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/api/agents/outside/ask/{id}", other)).Status);

        var (_, inbox) = await Get("/api/agents/approvals", admin);
        Assert.Equal("mcp", (string)inbox![0]!["origin"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/agents/approvals/{id}", other, new AnswerBody(true))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, $"/api/agents/approvals/{id}", admin, new AnswerBody(true))).Status);
        await Call(HttpMethod.Post, "/api/agents/outside/events", admin, new OutsideEventsBody([new("tool-called", "claude-code", "2.1", "build_model_add", "{}", null), new("policy-loosened", "claude-code", "2.1", null, null, "spoof")]));

        Assert.Equal("approved", (string)(await Get($"/api/agents/outside/ask/{id}", admin)).Body!["status"]!);
        var (_, audit) = await Get("/api/agents/audit?origin=mcp", admin);
        var kinds = audit!.AsArray().Select(a => (string)a!["kind"]!).Order().ToList();
        Assert.Equal(["approval-approved", "approval-asked", "tool-called"], kinds);   // the spoofed kind was not written
        Assert.All(audit.AsArray(), a => Assert.Equal(("Ada", "claude-code"), ((string)a!["personName"]!, (string)a["agent"]!)));
        Assert.Empty((await Get("/api/agents/audit?origin=mcp&person=Ada", viewer)).Body!.AsArray());   // a viewer's query is about themselves, whoever they name
    }

    // ---- knowledge: KN36, KN37, KN38, KN28 ----

    private const string Guardrail = "---\nknowledge: 1\ntitle: T\nrole: guardrail\nowner: administrators\nfor: { audience: back-end }\n---\nNever delete a production table.\n";

    [Fact]
    public async Task The_install_knowledge_project_is_made_from_the_template_and_a_policy_guardrail_is_edited_and_reaches_the_agent()
    {
        var (made, overview) = await Call(HttpMethod.Post, "/api/knowledge/install", admin);
        Assert.Equal(HttpStatusCode.OK, made);
        var install = overview!["projects"]![0]!;
        Assert.True((bool)install["exists"]!);
        Assert.True((int)install["items"]! >= 1);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/knowledge/install", admin)).Status);   // never over an existing one

        var (saved, _) = await Call(HttpMethod.Put, "/api/knowledge/items/install/policy/production.md", admin, new ItemBody(Guardrail));
        Assert.Equal(HttpStatusCode.OK, saved);
        var (_, chunks) = await Call(HttpMethod.Post, "/api/knowledge/preview/chunks", viewer, new ChunkPreviewBody("install", "policy/production.md", Guardrail));
        Assert.Equal("helper", (string)chunks!["reaches"]![0]!["agent"]!);
        Assert.True((int)chunks["reaches"]![0]!["adds"]! > 0);

        var (_, context) = await Call(HttpMethod.Post, "/api/knowledge/context", viewer, new ContextBody("helper", []));
        Assert.Contains(context!["parts"]!.AsArray(), p => (string)p!["id"]! == "policy/production.md" && (string)p["layer"]! == "install-policy");
        Assert.Equal(context["parts"]!.AsArray().Count, context["shares"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_policy_item_that_is_not_the_administrators_or_carries_a_prompting_key_is_refused_naming_where_it_belongs()
    {
        await Call(HttpMethod.Post, "/api/knowledge/install", admin);

        var (notAdmins, notAdminsBody) = await Call(HttpMethod.Put, "/api/knowledge/items/install/policy/x.md", admin, new ItemBody("---\nknowledge: 1\ntitle: T\nrole: guardrail\nowner: finance\n---\ntext\n"));
        var (prompting, promptingBody) = await Call(HttpMethod.Put, "/api/knowledge/items/install/policy/y.md", admin, new ItemBody("---\nknowledge: 1\ntitle: T\nrole: guardrail\nowner: administrators\nprompting: ask\n---\ntext\n"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notAdmins);
        Assert.Contains("administrators own it", (string)notAdminsBody!["diagnostics"]![0]!["message"]!);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, prompting);
        Assert.Contains("install policy", promptingBody!["diagnostics"]!.ToJsonString());
        Assert.False(File.Exists(Path.Combine(directory, "install-knowledge", "policy", "x.md")));
        Assert.False(File.Exists(Path.Combine(directory, "install-knowledge", "policy", "y.md")));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/knowledge/items/install/policy/z.md", viewer, new ItemBody(Guardrail))).Status);
    }

    [Fact]
    public async Task A_dropped_model_puts_its_items_in_the_owners_worklist_and_re_pointing_one_takes_it_off_and_retiring_removes_another()
    {
        await Call(HttpMethod.Put, "/api/knowledge/subjects", admin, new SubjectBody("semantic-model", "Sales", "exists"));
        await Call(HttpMethod.Put, "/api/knowledge/subjects", admin, new SubjectBody("semantic-model", "Revenue", "exists"));
        const string a = "---\nknowledge: 1\ntitle: T\nrole: instruction\nowner: finance\nabout:\n  - semantic-model: Sales\n---\nRevenue is net.\n";
        const string b = "---\nknowledge: 1\ntitle: T\nrole: instruction\nowner: finance\nabout:\n  - semantic-model: Sales\n---\nMargin is net of cost.\n";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/knowledge/items/project/revenue.md", admin, new ItemBody(a))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/knowledge/items/project/margin.md", admin, new ItemBody(b))).Status);
        var (unknown, unknownBody) = await Call(HttpMethod.Put, "/api/knowledge/items/project/bad.md", admin, new ItemBody(a.Replace("Sales", "Nowhere")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown);   // a subject the catalog never heard of is refused when saved
        Assert.Contains("Nowhere", unknownBody!.ToJsonString());

        var (_, dropped) = await Call(HttpMethod.Put, "/api/knowledge/subjects", admin, new SubjectBody("semantic-model", "Sales", "gone"));

        var list = Assert.Single(dropped!["worklists"]!.AsArray())!;
        Assert.Equal("finance", (string)list["owner"]!);
        Assert.Equal(["margin.md", "revenue.md"], list["work"]!.AsArray().Select(w => (string)w!["itemId"]!));
        Assert.All(list["work"]!.AsArray(), w => Assert.Equal("orphaned", (string)w!["kind"]!));

        var (_, repointed) = await Call(HttpMethod.Post, "/api/knowledge/repoint/project/revenue.md", admin, new RepointBody("semantic-model:Sales", "Revenue"));
        Assert.Equal(["margin.md"], repointed!["worklists"]![0]!["work"]!.AsArray().Select(w => (string)w!["itemId"]!));
        Assert.Contains("semantic-model: Revenue", (await Get("/api/knowledge/items/project/revenue.md", viewer)).Body!["text"]!.ToString());

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, "/api/knowledge/items/project/margin.md", admin)).Status);
        Assert.Empty((await Get("/api/knowledge/overview", viewer)).Body!["worklists"]!.AsArray());
    }

    [Fact]
    public async Task The_scope_preview_gives_what_applies_in_order_and_says_why_the_rest_does_not()
    {
        await Call(HttpMethod.Put, "/api/knowledge/items/project/k1.md", admin, new ItemBody("---\nknowledge: 1\ntitle: T\nrole: instruction\nowner: data\nabout:\n  - build-project: sales\nfor: { audience: back-end }\n---\nOne.\n"));
        await Call(HttpMethod.Put, "/api/knowledge/items/project/k3.md", admin, new ItemBody("---\nknowledge: 1\ntitle: T\nrole: instruction\nowner: data\nabout:\n  - semantic-model: Sales\n    measure: revenue\n---\nThree.\n"));
        await Call(HttpMethod.Put, "/api/knowledge/items/project/k6.md", admin, new ItemBody("---\nknowledge: 1\ntitle: T\nrole: instruction\nowner: data\nabout:\n  - install: true\nfor: { audience: front-end }\n---\nSix.\n"));

        var (_, body) = await Call(HttpMethod.Post, "/api/knowledge/preview/scope", viewer, new ScopePreviewBody(["build-project:sales", "semantic-model:Sales/measure:revenue"], "back-end", "helper", null));

        var preview = body!["preview"]!;
        Assert.Equal(["k1.md", "k3.md"], preview["instructions"]!.AsArray().Select(i => (string)i!["id"]!));
        Assert.Equal("k6.md", (string)preview["skipped"]![0]!["id"]!);
        Assert.StartsWith("Not for this audience", (string)preview["skipped"]![0]!["reason"]!);
    }

    [Fact]
    public async Task The_chunk_preview_marks_the_chunk_that_changed_against_the_saved_text_and_the_overview_says_there_is_no_index()
    {
        const string saved = "---\nknowledge: 1\ntitle: T\nrole: reference\nowner: data\nabout:\n  - install: true\n---\n# Guide\n\n## One\n\nFirst section text that is long enough to be its own chunk when the size is small.\n\n## Two\n\nSecond section text that is long enough to be its own chunk when the size is small.\n";
        await Call(HttpMethod.Put, "/api/knowledge/items/project/guide.md", admin, new ItemBody(saved));

        var (_, same) = await Call(HttpMethod.Post, "/api/knowledge/preview/chunks", viewer, new ChunkPreviewBody("project", "guide.md", saved));
        var (_, edited) = await Call(HttpMethod.Post, "/api/knowledge/preview/chunks", viewer, new ChunkPreviewBody("project", "guide.md", saved.Replace("First section text", "First section changed text")));

        Assert.Equal(0, (int)same!["preview"]!["changedChunks"]!);
        Assert.Equal(1, (int)edited!["preview"]!["changedChunks"]!);
        var (_, overview) = await Get("/api/knowledge/overview", viewer);
        Assert.False((bool)overview!["index"]!["built"]!);
        Assert.Contains("not part of this install", (string)overview["index"]!["message"]!);
        Assert.Equal(1, (int)overview["projects"]![1]!["items"]!);
    }

    [Fact]
    public async Task The_agents_module_is_one_of_the_consoles_state_modules()
    {
        var database = app.Services.GetRequiredService<StateDatabase>();
        Assert.Contains(database.RegisteredModules(), m => m.Module == "agents");
        await Task.CompletedTask;
    }
}
