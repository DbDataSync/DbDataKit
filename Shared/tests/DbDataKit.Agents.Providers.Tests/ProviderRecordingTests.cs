using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Providers;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests.Providers;

/// <summary>
/// Plan 27a AG04: the same checks against the recordings of four kinds of provider (OpenAI, Microsoft Foundry, Ollama and Anthropic; a local compatible server speaks OpenAI's wire and is the first one again). The recordings are in
/// <c>fixtures/</c>, written by hand from the providers' documented shapes and refreshed by hand, and nothing here opens a socket: a handler answers with the recorded file and keeps the request it was sent.
/// </summary>
public sealed class ProviderRecordingTests
{
    private const string Canary = "sk-canary-0123456789-do-not-log";

    /// <summary>One recording set: where it lives, which kind of provider it is, what the request must look like.</summary>
    public sealed record Recording(string Folder, string Kind, string Endpoint, string AuthStyle, string? Key, string Model, string Path, string? KeyHeader, int ErrorStatus, string ErrorFile)
    {
        public override string ToString() => Folder;
    }

    public static readonly Recording[] Recordings =
    [
        new("openai", "openai", "https://api.openai.com/v1", "bearer", Canary, "gpt-4.1", "/v1/chat/completions", null, 401, "error-401.json"),
        new("foundry", "foundry", "https://acme.openai.azure.com/openai/v1", "api-key", Canary, "gpt-4.1", "/openai/v1/chat/completions", "api-key", 401, "error-401.json"),
        new("ollama", "ollama", "http://localhost:11434/v1", "none", null, "llama3.1:8b", "/v1/chat/completions", null, 404, "error-404.json"),
        new("anthropic", "anthropic", "https://api.anthropic.com", "api-key", Canary, "claude-sonnet-4-5", "/v1/messages", "x-api-key", 401, "error-401.json"),
    ];

    public static TheoryData<Recording> Each => [.. Recordings];

    private static string Fixture(string folder, string file) => File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", folder, file));

    /// <summary>Answers each request from a queue of recorded responses and keeps what it was sent.</summary>
    private sealed class Recorded : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string Type)> _answers = new();

        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        public bool Unreachable { get; set; }

        public Recorded Answer(string body, HttpStatusCode status = HttpStatusCode.OK, string type = "application/json") { _answers.Enqueue((status, body, type)); return this; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Unreachable) throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            Sent.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation)));
            var (status, body, type) = _answers.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };
        }
    }

    private static ModelProviderDefinition Provider(Recording r) => Read(
        ModelProviderConnection.Load($"schema: connection/2\nkind: model-provider\nname: {r.Folder}-main\nendpoint: {r.Endpoint}\n{(r.Key is null ? "" : "sign_in: { method: api_key, key_secret: env:TEST_KEY }\n")}model_provider:\n  provider: {r.Kind}\n{(r.Kind == "openai-compatible" ? "  locality: local\n" : "")}  models:\n    - {{ id: {r.Model}, window: 128000 }}\n", "p.yml"));

    private static ModelProviderDefinition Read(ModelProviderResult result) => result.Definition ?? throw new InvalidOperationException(string.Join("; ", result.Diagnostics));

    private static IChatClient Client(Recording r, Recorded handler) =>
        ProviderClients.Create(Provider(r), Provider(r).Models[0], r.Key, new HttpClient(handler));

    private static readonly ChatMessage[] Ask = [new(ChatRole.System, "You help with things."), new(ChatRole.User, "show the widget")];

    private static ChatOptions WithTool() => new()
    {
        Tools = [AIFunctionFactory.CreateDeclaration("thing_show", "Show a thing", System.Text.Json.JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["properties"] = new JsonObject { ["args"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } },
        }))],
    };

    [Theory, MemberData(nameof(Each))]
    public async Task A_text_answer_comes_back_with_its_usage_and_the_request_has_the_right_address_key_and_model(Recording r)
    {
        var handler = new Recorded().Answer(Fixture(r.Folder, "text.json"));

        var response = await Client(r, handler).GetResponseAsync(Ask, WithTool());

        Assert.Equal("ok", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.True(response.Usage!.InputTokenCount > 0 && response.Usage.OutputTokenCount > 0);
        var (request, body) = handler.Sent.Single();
        Assert.Equal(r.Path, request.RequestUri!.AbsolutePath);
        var json = JsonNode.Parse(body)!;
        Assert.Equal(r.Model, (string?)json["model"]);
        Assert.DoesNotContain(Canary, body);                                        // the key is a header, never part of the body
        if (r.KeyHeader is not null) Assert.Equal(Canary, request.Headers.GetValues(r.KeyHeader).Single());
        else if (r.Key is not null) Assert.Equal("Bearer " + Canary, request.Headers.Authorization!.ToString());
        else Assert.Null(request.Headers.Authorization);
        if (r.Kind == "anthropic")
        {
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Equal("You help with things.", (string?)json["system"]);          // a system message is the top-level field
            Assert.NotNull(json["max_tokens"]);                                       // required on every call
            Assert.Equal("thing_show", (string?)json["tools"]![0]!["name"]);
            Assert.Equal("object", (string?)json["tools"]![0]!["input_schema"]!["type"]);
        }
        else
        {
            Assert.Equal("system", (string?)json["messages"]![0]!["role"]);
            Assert.Equal("function", (string?)json["tools"]![0]!["type"]);
            Assert.Equal("thing_show", (string?)json["tools"]![0]!["function"]!["name"]);
            Assert.Equal("object", (string?)json["tools"]![0]!["function"]!["parameters"]!["type"]);
        }
    }

    [Theory, MemberData(nameof(Each))]
    public async Task A_tool_call_arrives_with_its_arguments_read_and_the_finish_reason_says_so(Recording r)
    {
        var handler = new Recorded().Answer(Fixture(r.Folder, "toolcall.json"));

        var response = await Client(r, handler).GetResponseAsync(Ask, WithTool());

        var call = Assert.Single(response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("thing_show", call.Name);
        Assert.False(string.IsNullOrEmpty(call.CallId));
        Assert.Null(call.Exception);
        Assert.Equal("""{"args":["widget"]}""", System.Text.Json.JsonSerializer.Serialize(call.Arguments));
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    [Theory, MemberData(nameof(Each))]
    public async Task The_next_request_carries_the_call_and_its_result_in_the_providers_own_shape(Recording r)
    {
        var handler = new Recorded().Answer(Fixture(r.Folder, "toolcall.json")).Answer(Fixture(r.Folder, "text.json"));
        var client = Client(r, handler);
        var conversation = new List<ChatMessage>(Ask);
        var first = await client.GetResponseAsync(conversation, WithTool());
        conversation.AddRange(first.Messages);
        var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        conversation.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "<tool_result>a widget</tool_result>")]));

        var second = await client.GetResponseAsync(conversation, WithTool());

        Assert.Equal("ok", second.Text);
        var json = JsonNode.Parse(handler.Sent[1].Body)!["messages"]!.AsArray();
        if (r.Kind == "anthropic")
        {
            Assert.Equal(["user", "assistant", "user"], json.Select(m => (string?)m!["role"]));
            Assert.Equal("tool_use", (string?)json[1]!["content"]!.AsArray().Last()!["type"]);
            var result = json[2]!["content"]![0]!;
            Assert.Equal(("tool_result", call.CallId), ((string?)result["type"], (string?)result["tool_use_id"]));
            Assert.Equal("<tool_result>a widget</tool_result>", (string?)result["content"]);
        }
        else
        {
            Assert.Equal(["system", "user", "assistant", "tool"], json.Select(m => (string?)m!["role"]));
            var sentCall = json[2]!["tool_calls"]![0]!;
            Assert.Equal(call.CallId, (string?)sentCall["id"]);
            Assert.Equal("""{"args":["widget"]}""", (string?)sentCall["function"]!["arguments"]);     // a JSON string, as the wire has it
            Assert.Equal(call.CallId, (string?)json[3]!["tool_call_id"]);
        }
    }

    [Theory, MemberData(nameof(Each))]
    public async Task A_stream_gives_the_text_as_it_comes_then_the_whole_call_and_the_usage(Recording r)
    {
        var handler = new Recorded().Answer(Fixture(r.Folder, "stream-toolcall.sse"), type: "text/event-stream");

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in Client(r, handler).GetStreamingResponseAsync(Ask, WithTool())) updates.Add(update);

        Assert.True(JsonNode.Parse(handler.Sent.Single().Body)!["stream"]!.GetValue<bool>());
        Assert.StartsWith(r.Kind switch { "anthropic" => "I'll look", "ollama" => "Sure.", _ => "Looking" }, string.Concat(updates.Select(u => u.Text)));
        Assert.True(updates.Count(u => u.Text.Length > 0) >= 1);
        var call = Assert.Single(updates.SelectMany(u => u.Contents).OfType<FunctionCallContent>());
        Assert.Equal("thing_show", call.Name);
        Assert.Null(call.Exception);
        Assert.Equal("""{"args":["widget"]}""", System.Text.Json.JsonSerializer.Serialize(call.Arguments));
        Assert.Equal(ChatFinishReason.ToolCalls, updates.Last().FinishReason);
        if (r.Kind != "ollama") Assert.True(updates.SelectMany(u => u.Contents).OfType<UsageContent>().Single().Details.InputTokenCount > 0);   // a local server may report none in a stream
        var all = updates.ToChatResponse();
        Assert.Single(all.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
    }

    [Theory, MemberData(nameof(Each))]
    public async Task A_refused_key_or_model_is_a_message_in_words_that_is_not_temporary_and_never_holds_the_key(Recording r)
    {
        var handler = new Recorded().Answer(Fixture(r.Folder, r.ErrorFile), (HttpStatusCode)r.ErrorStatus);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Client(r, handler).GetResponseAsync(Ask));

        Assert.False(ex.Temporary);
        Assert.StartsWith($"{r.Folder}-main ", ex.Message);
        Assert.DoesNotContain(Canary, ex.ToString());
        Assert.Contains(r.ErrorStatus == 404 ? "model" : "key", ex.Message);
        Assert.False(ModelProviderException.WorthRetrying(ex));
    }

    [Theory, MemberData(nameof(Each))]
    public async Task A_provider_that_is_busy_or_down_is_temporary_so_the_slot_retries_and_falls_through(Recording r)
    {
        var busy = r.Kind == "anthropic" ? Fixture("anthropic", "overloaded.json") : Fixture("openai", "error-429.json");
        var handler = new Recorded().Answer(busy, (HttpStatusCode)(r.Kind == "anthropic" ? 529 : 429));
        var ex = await Assert.ThrowsAsync<ModelProviderException>(() => Client(r, handler).GetResponseAsync(Ask));
        Assert.True(ex.Temporary);
        Assert.True(ModelProviderException.WorthRetrying(ex));

        var down = await Assert.ThrowsAsync<ModelProviderException>(() => Client(r, new Recorded { Unreachable = true }).GetResponseAsync(Ask));
        Assert.True(down.Temporary);
        Assert.Contains("could not be reached", down.Message);
        Assert.DoesNotContain(Canary, down.ToString());
    }

    [Fact]
    public async Task A_call_whose_arguments_are_cut_off_arrives_carrying_the_parse_error_so_the_runner_can_tell_the_model()
    {
        var r = Recordings[0];
        var handler = new Recorded().Answer(Fixture("openai", "malformed-call.json"));

        var response = await Client(r, handler).GetResponseAsync(Ask, WithTool());

        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.NotNull(call.Exception);
        Assert.Equal("call_bad", call.CallId);
    }

    [Theory]
    [InlineData("openai", "refusal.json")]
    [InlineData("anthropic", "refusal.json")]
    public async Task A_refusal_is_the_content_filter_finish_with_the_words(string folder, string file)
    {
        var r = Recordings.Single(x => x.Folder == folder);
        var response = await Client(r, new Recorded().Answer(Fixture(folder, file))).GetResponseAsync(Ask);

        Assert.Equal(ChatFinishReason.ContentFilter, response.FinishReason);
        Assert.Contains("can't help", response.Text);
    }

    [Fact]
    public async Task The_agent_loop_runs_a_tool_over_a_recorded_conversation_and_ends_with_the_answer()
    {
        var r = Recordings[3];
        var handler = new Recorded().Answer(Fixture("anthropic", "toolcall.json")).Answer(Fixture("anthropic", "text.json"));
        var ran = new List<string>();
        var router = new NounRouter("demo",
        [
            new Noun("thing", "Things", [new Verb("show", "Show a thing", EffectClass.OfflineOnly, "<name>", async (a, c) => { ran.Add(string.Join(' ', a)); await c.Out.WriteLineAsync("a widget"); return 0; }, DataSchemas.None)]),
        ], "1");
        var tools = CommandTools.From(router, new McpOptions(new HashSet<string>()));

        var run = await new AgentRunner(Client(r, handler), tools).RunAsync([new ChatMessage(ChatRole.User, "show the widget")], new AgentRunContext("alice", "demo-agent", "1"));

        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.Equal("ok", run.Text);
        Assert.Equal(["widget"], ran);
        Assert.Equal(2, run.Usage.ModelTurns);
        Assert.Equal(86 + 14, run.Usage.InputTokens);
    }

    [Fact]
    public async Task Test_says_what_the_model_answered_or_why_it_did_not_in_words()
    {
        var r = Recordings[0];
        var p = Provider(r);
        var ok = await ProviderClients.TestAsync(p, p.Models[0], r.Key, new HttpClient(new Recorded().Answer(Fixture("openai", "text.json"))));
        Assert.True(ok.Ok);
        Assert.Equal("ok", ok.Answer);
        Assert.Equal(17, ok.InputTokens);

        var down = await ProviderClients.TestAsync(p, p.Models[0], r.Key, new HttpClient(new Recorded { Unreachable = true }));
        Assert.False(down.Ok);
        Assert.True(down.Temporary);
        Assert.Contains("could not be reached", down.Message);
        Assert.Contains("Check that the endpoint is right", down.Message);

        var refused = await ProviderClients.TestAsync(p, p.Models[0], r.Key, new HttpClient(new Recorded().Answer(Fixture("openai", "error-401.json"), HttpStatusCode.Unauthorized)));
        Assert.False(refused.Ok);
        Assert.Contains("did not accept the key", refused.Message);
        Assert.DoesNotContain(Canary, refused.Message);
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(refused));
    }
}
