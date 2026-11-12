using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a AG23 and AG24 on a real console with a scripted model and a scripted judge: the cases are files of the agent, a run puts each to the agent as a conversation, checks it (the suite decides what it can, the judge reads only the rubric)
/// and keeps the agent version, the model, the score per case and the cost; a rubric with no judge bound is not judged; the minimum score before a promotion is a recorded choice and a promotion below it is refused naming the score.
/// </summary>
public sealed class ConsoleEvaluationTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-eval-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private readonly FakeModels models = new();
    private string admin = "";
    private string viewer = "";

    private sealed class FakeModels : IAgentModels
    {
        // one scripted client for the agent: the cases run one after another in the order of their files, each taking the steps it needs
        public ScriptedChatClient? Agent { get; set; }

        public ScriptedChatClient? Judge { get; set; }

        public BoundModel? For(string slot, string? channel)
        {
            if (slot == "judge") return Judge is { } j ? new BoundModel(j, "judge-model", new ModelPrice(1m, 1m), 10_000) : null;
            return Agent is { } a ? new BoundModel(a, "scripted", new ModelPrice(1m, 2m), 10_000) : null;
        }
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
        var agent = Path.Combine(directory, "agents", "helper");
        Directory.CreateDirectory(Path.Combine(agent, "instructions"));
        Directory.CreateDirectory(Path.Combine(agent, "tests", "evaluation"));
        File.WriteAllText(Path.Combine(agent, "agent.yml"), "agent: 1\nname: helper\ntitle: Helper\naudience: back-end\nmodel: { slot: default }\ninstructions: [instructions/role.md]\ntools:\n  - command: thing show\n");
        File.WriteAllText(Path.Combine(agent, "instructions", "role.md"), "You help with things. Be brief.\n");
        File.WriteAllText(Path.Combine(agent, "tests", "evaluation", "widget.yml"), "evaluation: 1\ntask: What is the widget?\npass-at: 0.8\nexpect:\n  - tools-called: thing_show\n  - answer-contains: widget\n  - rubric: The answer says what the widget is.\n");
        File.WriteAllText(Path.Combine(agent, "tests", "evaluation", "gadget.yml"), "evaluation: 1\ntask: What is the gadget?\nexpect:\n  - answer-contains: gadget\n");

        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services =>
        {
            services.AddSingleton<IAgentModels>(models);
            services.AddSingleton<IAgentToolSource, DemoTools>();
        });
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
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

    private async Task<JsonNode> Evaluate()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/agents/helper/evaluation/run", admin);
        Assert.True(status == HttpStatusCode.Accepted, body?.ToJsonString());
        await app.Services.GetRequiredService<ConsoleEvaluation>().WhenIdle("helper");
        return (await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!;
    }

    private static ScriptStep[] Right() => [ScriptStep.Call("thing_show", new { args = new[] { "widget" } }).WithUsage(500, 40), ScriptStep.Say("It is a widget.").WithUsage(600, 60)];

    private static ScriptStep[] Wrong() => [ScriptStep.Say("I do not know.").WithUsage(300, 20)];

    [Fact]
    public async Task The_cases_are_listed_with_what_each_expects_and_anyone_signed_in_may_read_them()
    {
        var (status, body) = await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gadget", "widget"], body!["cases"]!.AsArray().Select(c => (string)c!["name"]!).Order());
        var widget = body["cases"]!.AsArray().Single(c => (string)c!["name"]! == "widget")!;
        Assert.True((bool)widget["hasRubric"]!);
        Assert.Equal(["tools-called: thing_show", "answer-contains: widget", "rubric: The answer says what the widget is."], widget["checks"]!.AsArray().Select(c => (string)c!));
        Assert.False((bool)body["judgeBound"]!);
        Assert.Empty(body["runs"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/agents/nobody/evaluation", viewer)).Status);
    }

    [Fact]
    public async Task A_run_puts_each_case_to_the_agent_checks_it_with_the_suite_and_the_judge_and_keeps_the_facts_of_the_run()
    {
        models.Judge = new ScriptedChatClient(ScriptStep.Say("""{"score": 0.5, "reasoning": "It names the widget but not what it is for."}"""));
        models.Agent = new ScriptedChatClient([.. Wrong(), .. Right()]);        // gadget first (the order of the files), then widget

        var state = await Evaluate();

        var run = Assert.Single(state["runs"]!.AsArray())!;
        Assert.Equal(2, (int)run["total"]!);
        Assert.True((int)run["passed"]! == 1, (await Call(HttpMethod.Get, $"/api/agents/helper/evaluation/runs/{(string)run["id"]!}", viewer)).Body!.ToJsonString());
        Assert.Equal(("scripted", "main", "manual"), ((string)run["model"]!, (string)run["channel"]!, (string)run["trigger"]!));
        Assert.Equal((string)state["version"]!, (string)run["agentVersion"]!);
        Assert.True((decimal)run["cost"]! > 0);

        var (_, detail) = await Call(HttpMethod.Get, $"/api/agents/helper/evaluation/runs/{(string)run["id"]!}", viewer);
        var cases = detail!["cases"]!.AsArray().ToDictionary(c => (string)c!["name"]!, c => c!);
        Assert.Equal((0m, false), ((decimal)cases["gadget"]["score"]!, (bool)cases["gadget"]["passed"]!));
        // widget: the two checks of the suite hold and the judge gave 0.5, so the case scores a half and a third more: (1 + 1 + 0.5) / 3
        Assert.Equal(Math.Round(2.5m / 3, 4), (decimal)cases["widget"]["score"]!);
        var checks = cases["widget"]["checks"]!.AsArray().ToDictionary(c => (string)c!["kind"]!, c => c!);
        Assert.Equal("suite", (string)checks["tools-called"]["method"]!);
        Assert.Equal(("judge", "It names the widget but not what it is for."), ((string)checks["rubric"]["method"]!, (string)checks["rubric"]["judgement"]!));
        Assert.Equal("It is a widget.", (string)cases["widget"]["answer"]!);
        Assert.Equal((1100L, 100L), ((long)cases["widget"]["inputTokens"]!, (long)cases["widget"]["outputTokens"]!));
    }

    [Fact]
    public async Task With_no_judge_bound_a_rubric_is_not_judged_and_does_not_count()
    {
        models.Agent = new ScriptedChatClient(Right());
        File.Delete(Path.Combine(directory, "agents", "helper", "tests", "evaluation", "gadget.yml"));

        var state = await Evaluate();

        var run = state["runs"]![0]!;
        var (_, detail) = await Call(HttpMethod.Get, $"/api/agents/helper/evaluation/runs/{(string)run["id"]!}", viewer);
        var widget = detail!["cases"]![0]!;
        Assert.Equal(1m, (decimal)widget["score"]!);
        var rubric = widget["checks"]!.AsArray().Single(c => (string)c!["kind"]! == "rubric")!;
        Assert.Null(rubric["score"]);
        Assert.Contains("No judge model is bound", (string)rubric["message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_administrator_starts_a_run_a_case_that_does_not_read_stops_it_and_no_model_is_said()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/agents/helper/evaluation/run", viewer)).Status);

        var noModel = await Call(HttpMethod.Post, "/api/agents/helper/evaluation/run", admin);
        Assert.Equal(HttpStatusCode.Accepted, noModel.Status);                          // started: each case then says it had no model
        await app.Services.GetRequiredService<ConsoleEvaluation>().WhenIdle("helper");
        var state = (await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!;
        var (_, detail) = await Call(HttpMethod.Get, $"/api/agents/helper/evaluation/runs/{(string)state["runs"]![0]!["id"]!}", viewer);
        Assert.All(detail!["cases"]!.AsArray(), c => Assert.Contains("No model is bound", (string)c!["problem"]!, StringComparison.Ordinal));
        Assert.All(detail["cases"]!.AsArray(), c => Assert.Equal(0m, (decimal)c!["score"]!));

        File.WriteAllText(Path.Combine(directory, "agents", "helper", "tests", "evaluation", "broken.yml"), "evaluation: 1\ntask: t\nexpect:\n  - answer-contain: x\n");
        var (status, body) = await Call(HttpMethod.Post, "/api/agents/helper/evaluation/run", admin);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("does not read", (string)body!["error"]!, StringComparison.Ordinal);
        var listed = (await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!;
        Assert.Contains("Did you mean `answer-contains`", (string)listed["diagnostics"]![0]!["message"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_minimum_is_a_recorded_choice_and_a_promotion_below_it_is_refused_naming_the_score()
    {
        // no minimum: nothing is refused
        var before = (await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!;
        Assert.Null(before["promotion"]!["refusal"]);
        Assert.Null(before["minimum"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/agents/helper/evaluation/minimum", viewer, new MinimumBody(0.8m))).Status);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Call(HttpMethod.Put, "/api/agents/helper/evaluation/minimum", admin, new MinimumBody(1.5m))).Status);
        var (_, set) = await Call(HttpMethod.Put, "/api/agents/helper/evaluation/minimum", admin, new MinimumBody(0.8m));
        Assert.Contains("has not been evaluated there", (string)set!["promotion"]!["refusal"]!, StringComparison.Ordinal);

        // evaluated, and below the minimum: refused, naming the score and how many cases passed
        models.Judge = new ScriptedChatClient(ScriptStep.Say("""{"score": 0.5, "reasoning": "partly"}"""));
        models.Agent = new ScriptedChatClient([.. Wrong(), .. Right()]);
        var state = await Evaluate();
        var refusal = (string)state["promotion"]!["refusal"]!;
        Assert.Contains("helper", refusal, StringComparison.Ordinal);
        Assert.Contains("below the minimum of 0.80 set for main", refusal, StringComparison.Ordinal);
        Assert.Contains("(1 of 2 cases passed)", refusal, StringComparison.Ordinal);
        Assert.Equal(0.8m, (decimal)state["minimum"]!["minimum"]!);

        // the choice is changed to one the score reaches, and the promotion goes on; removing it asks nothing
        await Call(HttpMethod.Put, "/api/agents/helper/evaluation/minimum", admin, new MinimumBody(0.4m));
        Assert.Null((await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!["promotion"]!["refusal"]);
        await Call(HttpMethod.Put, "/api/agents/helper/evaluation/minimum", admin, new MinimumBody(null));
        Assert.Null((await Call(HttpMethod.Get, "/api/agents/helper/evaluation", viewer)).Body!["minimum"]);
    }
}
