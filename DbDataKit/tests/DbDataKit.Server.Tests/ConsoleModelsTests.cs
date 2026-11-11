using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a AG05 on a real console: a provider file and a pasted key (kept in the secret store, never in an answer or the record), a Test that calls the provider (here a handler that answers with a recorded Anthropic message), slots bound per
/// channel with a fallback, and a conversation that starts and runs over that binding, with a person approving the tool call. No socket is opened.
/// </summary>
public sealed class ConsoleModelsTests : IAsyncLifetime
{
    private const string Canary = "sk-ant-canary-0123456789-do-not-log";

    private const string ToolCall = """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"I'll look."},{"type":"tool_use","id":"toolu_1","name":"thing_show","input":{"args":["widget"]}}],"stop_reason":"tool_use","usage":{"input_tokens":86,"output_tokens":41}}""";

    private const string Text = """{"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"It is a widget."}],"stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":6}}""";

    private const string Provider = "schema: connection/2\nkind: model-provider\nname: claude\nsign_in: { method: api_key, key_secret: secret:dbdata:console:connection:claude:key }\nmodel_provider:\n  provider: anthropic\n  models:\n    - { id: claude-sonnet-4-5, window: 200000, max_output: 8192, price: { input: 3, output: 15 } }\n";

    private const string Slots = "model-slots: 1\nslots:\n  default:\n    \"*\":\n      - { provider: claude, model: claude-sonnet-4-5 }\n";

    private sealed class Answers : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Queue { get; } = new();

        public List<(string Path, string? Key, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Sent.Add((request.RequestUri!.AbsolutePath, request.Headers.TryGetValues("x-api-key", out var k) ? k.Single() : null, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation)));
            var (status, body) = Queue.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class DemoTools : IAgentToolSource
    {
        public NounRouter Router() => new("demo",
        [
            new Noun("thing", "Things", [new Verb("show", "Show a thing", EffectClass.OfflineOnly, "<name>", async (a, c) => { c.Data["name"] = a.FirstOrDefault(); await c.Out.WriteLineAsync("a widget"); return 0; }, DataSchemas.Object([("name", DataSchemas.String)]))]),
        ], "1");

        public McpOptions Options { get; } = new(new HashSet<string>());
    }

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-models-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Answers answers = new();
    private readonly List<string> bodies = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";

    public async ValueTask InitializeAsync()
    {
        var agent = Path.Combine(directory, "agents", "helper");
        Directory.CreateDirectory(Path.Combine(agent, "instructions"));
        File.WriteAllText(Path.Combine(agent, "agent.yml"), "agent: 1\nname: helper\ntitle: Helper\naudience: back-end\nmodel: { slot: default }\ninstructions: [instructions/role.md]\ntools:\n  - command: thing show\n    prompting: ask\n");
        File.WriteAllText(Path.Combine(agent, "instructions", "role.md"), "You help with things. Be brief.\n");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services =>
        {
            services.AddSingleton<IAgentToolSource, DemoTools>();
            services.AddSingleton(new ModelHttp(answers));
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

    /// <summary>Calls the API and keeps every body that came back, so a test can say the key is in none of them.</summary>
    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        bodies.Add(text);
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Get(string path, string? session) => Call(HttpMethod.Get, path, session);

    [Fact]
    public async Task A_provider_a_key_and_a_slot_bind_a_model_and_a_conversation_runs_over_it_with_the_person_approving_the_tool_call()
    {
        // nothing bound: the slot the agent asks for is listed as unbound, and a conversation does not start
        var (_, empty) = await Get("/api/agents/models", admin);
        Assert.Empty(empty!["providers"]!.AsArray());
        Assert.False((bool)empty["agentSlots"]![0]!["bound"]!);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper"))).Status);

        // a provider whose key is written as itself is refused, and the refusal does not repeat it
        var (refused, bad) = await Call(HttpMethod.Put, "/api/outside-connections/claude", admin, new YamlBody(Provider.Replace("secret:dbdata:console:connection:claude:key", Canary)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains("secret reference", (string)bad!["diagnostics"]![0]!["message"]!);
        Assert.DoesNotContain(Canary, bad.ToJsonString());

        // the provider is saved, and its key is not yet there
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/claude", admin, new YamlBody(Provider))).Status);
        var (_, before) = await Get("/api/agents/models", admin);
        Assert.False((bool)before!["providers"]![0]!["keyAvailable"]!);
        Assert.Contains("is not set", (string)before["providers"]![0]!["keyProblem"]!);
        var untested = await Call(HttpMethod.Post, "/api/agents/models/providers/claude/test", admin, new TestModelBody("claude-sonnet-4-5"));
        Assert.False((bool)untested.Body!["ok"]!);

        // the key is kept in the secret store; only an administrator may set it
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/outside-connections/claude/secrets/key", viewer, new SecretBody(Canary))).Status);
        var (stored, keyAnswer) = await Call(HttpMethod.Put, "/api/outside-connections/claude/secrets/key", admin, new SecretBody(Canary));
        Assert.Equal(HttpStatusCode.OK, stored);
        Assert.Equal("secret:dbdata:console:connection:claude:key", (string)keyAnswer!["reference"]!);
        Assert.DoesNotContain(Canary, File.ReadAllText(Path.Combine(directory, "connections", "claude.yml")));

        // Test calls the provider with the key and says what the model answered
        answers.Queue.Enqueue((HttpStatusCode.OK, """{"id":"msg_0","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":14,"output_tokens":4}}"""));
        var (_, tested) = await Call(HttpMethod.Post, "/api/agents/models/providers/claude/test", admin, new TestModelBody("claude-sonnet-4-5"));
        Assert.True((bool)tested!["ok"]!);
        Assert.Equal("ok", (string)tested["answer"]!);
        Assert.Equal(("/v1/messages", Canary), (answers.Sent[0].Path, answers.Sent[0].Key));
        answers.Queue.Enqueue((HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
        var (_, refusedKey) = await Call(HttpMethod.Post, "/api/agents/models/providers/claude/test", admin, new TestModelBody("claude-sonnet-4-5"));
        Assert.False((bool)refusedKey!["ok"]!);
        Assert.Contains("did not accept the key", (string)refusedKey["message"]!);

        // a slot that names a model the provider does not allow is refused with the line; the right one binds the install
        var (slotRefused, slotBad) = await Call(HttpMethod.Put, "/api/agents/models/slots", admin, new YamlBody(Slots.Replace("claude-sonnet-4-5", "claude-9")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, slotRefused);
        Assert.Contains("claude-9", (string)slotBad!["diagnostics"]![0]!["message"]!);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/agents/models/slots", admin, new YamlBody(Slots))).Status);
        var (_, bound) = await Get("/api/agents/models", viewer);
        Assert.True((bool)bound!["agentSlots"]![0]!["bound"]!);
        Assert.True((bool)bound["bindings"]![0]!["choices"]![0]!["usable"]!);

        // a conversation starts, asks the person before the tool, and ends with the answer
        answers.Queue.Enqueue((HttpStatusCode.OK, ToolCall));
        answers.Queue.Enqueue((HttpStatusCode.OK, Text));
        var (started, conversation) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("helper"));
        Assert.Equal(HttpStatusCode.OK, started);
        var id = (string)conversation!["id"]!;
        await Call(HttpMethod.Post, $"/api/agents/conversations/{id}/messages", admin, new SendMessageBody("what is widget?"));
        JsonNode? pending = null;
        for (var i = 0; i < 200 && pending is null; i++) { pending = (await Get("/api/agents/approvals", admin)).Body!.AsArray().FirstOrDefault(); if (pending is null) await Task.Delay(50); }
        Assert.NotNull(pending);
        Assert.Equal("thing_show", (string)pending["tool"]!);
        await Call(HttpMethod.Post, $"/api/agents/approvals/{(string)pending["id"]!}", admin, new AnswerBody(true));
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(id);

        var (_, view) = await Get($"/api/agents/conversations/{id}", admin);
        Assert.Equal("It is a widget.", (string)view!["text"]!.AsArray().Last()!["content"]!);
        Assert.Equal("claude/claude-sonnet-4-5", (string)view["turns"]![0]!["model"]!);
        Assert.Equal(86, (int)view["turns"]![0]!["inputTokens"]!);
        Assert.True((decimal)view["turns"]![0]!["cost"]! > 0);                       // the price on the provider's file times what the provider reported
        // the second request carried the tool's result back, as data, in Anthropic's shape; the key was in the header of each call and in no body
        var second = JsonNode.Parse(answers.Sent.Last().Body)!;
        Assert.Equal("tool_result", (string)second["messages"]!.AsArray().Last()!["content"]![0]!["type"]!);
        Assert.All(answers.Sent, s => Assert.Equal(Canary, s.Key));
        Assert.All(answers.Sent, s => Assert.DoesNotContain(Canary, s.Body));

        // the canary: the key is in no answer of the API, no file of the console, no row of the record
        var (_, audit) = await Get("/api/agents/audit?limit=1000", admin);
        Assert.DoesNotContain(Canary, audit!.ToJsonString());
        Assert.DoesNotContain(Canary, string.Join('\n', bodies));
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("secrets.yaml", StringComparison.Ordinal) && !f.Contains("keys") && !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-")))
            Assert.DoesNotContain(Canary, await File.ReadAllTextAsync(file));
        Assert.Contains(audit.AsArray(), a => (string)a!["kind"]! == "connection-secret-set");
    }

    [Fact]
    public async Task A_slot_bound_to_two_models_falls_to_the_second_when_the_first_is_down_and_a_channel_binds_differently()
    {
        Directory.CreateDirectory(Path.Combine(directory, "connections"));
        File.WriteAllText(Path.Combine(directory, "connections", "claude.yml"), Provider);
        File.WriteAllText(Path.Combine(directory, "connections", "local.yml"), "schema: connection/2\nkind: model-provider\nname: local\nmodel_provider:\n  provider: ollama\n  models:\n    - { id: llama3.1:8b, window: 8000 }\n");
        await Call(HttpMethod.Put, "/api/outside-connections/claude/secrets/key", admin, new SecretBody(Canary));
        var slots = "model-slots: 1\nslots:\n  default:\n    \"*\":\n      - { provider: claude, model: claude-sonnet-4-5 }\n      - { provider: local, model: \"llama3.1:8b\" }\n    test:\n      - { provider: local, model: \"llama3.1:8b\" }\n";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/agents/models/slots", admin, new YamlBody(slots))).Status);

        var models = app.Services.GetRequiredService<ConsoleModels>();
        Assert.Equal("claude/claude-sonnet-4-5", models.For("default", null)!.Model);
        Assert.Equal("local/llama3.1:8b", models.For("default", "test")!.Model);                  // a channel's own binding
        Assert.Equal("claude/claude-sonnet-4-5", models.For("default", "prod")!.Model);          // another channel has the one for every channel
        Assert.Null(models.For("other", null));

        // the first model is busy for every attempt; the answer comes from the second
        for (var i = 0; i < 3; i++) answers.Queue.Enqueue(((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));
        answers.Queue.Enqueue((HttpStatusCode.OK, """{"id":"chatcmpl-1","object":"chat.completion","model":"llama3.1:8b","choices":[{"index":0,"message":{"role":"assistant","content":"from the local model"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""));
        var bound = models.For("default", null)!;
        var response = await bound.Client.GetResponseAsync([new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi")]);
        Assert.Equal("from the local model", response.Text);
        Assert.Equal(["/v1/messages", "/v1/messages", "/v1/messages", "/v1/chat/completions"], answers.Sent.Select(s => s.Path));
    }
}
