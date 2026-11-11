using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

/// <summary>
/// <c>ui mcp --report-to</c> (plan 27a AG29, the outside half): the session is posted to the console as the person whose token the server holds, and a client that cannot ask the person asks in the console's inbox. The console is a
/// scripted handler that keeps what it was sent and answers the poll as a person would.
/// </summary>
public sealed class McpReportTests
{
    private sealed class FakeConsole : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body, string? Authorization)> Requests { get; } = [];
        public string[] Answers { get; set; } = ["pending", "approved"];
        public bool Down { get; set; }
        private int _polls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (Down) throw new HttpRequestException("connection refused");
            string body = request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellation).GetAwaiter().GetResult();
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/outside/ask", StringComparison.Ordinal)) return Task.FromResult(Json("""{"id":"11111111-1111-1111-1111-111111111111"}"""));
            if (path.Contains("/outside/ask/", StringComparison.Ordinal))
            {
                string status = Answers[Math.Min(_polls++, Answers.Length - 1)];
                return Task.FromResult(Json($$"""{"id":"11111111-1111-1111-1111-111111111111","status":"{{status}}","answeredBy":"alice","note":null}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    private sealed class Product
    {
        public List<string> Ran { get; } = [];

        public NounRouter Router() => new("demo",
        [
            new Noun("workspace", "A workspace",
            [
                new Verb("show", "Show the workspace", EffectClass.OfflineOnly, "", (a, c) => { Ran.Add("show"); return Task.FromResult(0); }, DataSchemas.None),
                new Verb("edit", "edit the draft", EffectClass.RepoFilesOnly, "<arg>", (a, c) => { Ran.Add($"edit {string.Join(' ', a)}"); return Task.FromResult(0); }, DataSchemas.None),
            ]),
        ], "1");
    }

    private static string Initialize(bool elicitation) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["clientInfo"] = new JsonObject { ["name"] = "scripted-agent", ["version"] = "0.3" },
                ["capabilities"] = elicitation ? new JsonObject { ["elicitation"] = new JsonObject() } : new JsonObject(),
            },
        }.ToJsonString();

    private static string Call(int id, string tool, params string[] args) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject { ["args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a)!)]) } },
        }.ToJsonString();

    private static async Task<(List<JsonObject> Lines, Product Product, StringWriter Log)> Run(FakeConsole console, bool elicitation, params string[] script)
    {
        var product = new Product();
        var log = new StringWriter();
        var reporter = ConsoleReporter.Create(new Uri("https://console.test/"), "alice", "s3cret", log, console, TimeSpan.FromMilliseconds(5));
        var gate = new McpGate("alice", null, new CompositeAudit(new McpAuditLog(), reporter), null, reporter.AskInboxAsync);
        var server = new McpServer(product.Router(), new McpOptions(new HashSet<string>(), Gate: gate), "demo", "1");
        var output = new StringWriter();
        await server.ServeAsync(new StringReader(string.Join('\n', script.Prepend(Initialize(elicitation)))), output);
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList(), product, log);
    }

    private static List<JsonNode> Events(FakeConsole console) =>
        console.Requests.Where(r => r.Path.EndsWith("/outside/events", StringComparison.Ordinal))
            .SelectMany(r => JsonNode.Parse(r.Body)!["events"]!.AsArray().Select(e => e!.DeepClone())).ToList();

    private static bool IsError(List<JsonObject> lines, int id) => (bool)lines.Single(l => l["id"] is JsonValue v && v.TryGetValue<int>(out var i) && i == id)["result"]!["isError"]!;

    [Fact]
    public async Task A_client_that_cannot_ask_asks_in_the_inbox_and_the_step_runs_after_the_person_approves_there()
    {
        var console = new FakeConsole();

        var (lines, product, _) = await Run(console, elicitation: false, Call(10, "workspace_edit", "draft-1"));

        Assert.False(IsError(lines, 10));
        Assert.Equal(["edit draft-1"], product.Ran);
        Assert.DoesNotContain(lines, l => (string?)l["method"] == "elicitation/create");
        var ask = console.Requests.Single(r => r.Method == "POST" && r.Path == "/api/agents/outside/ask");
        var body = JsonNode.Parse(ask.Body)!;
        Assert.Equal("scripted-agent", (string?)body["agent"]);
        Assert.Equal("0.3", (string?)body["agentVersion"]);
        Assert.Equal("workspace_edit", (string?)body["tool"]);
        Assert.Contains("workspace edit draft-1", (string?)body["preview"]);
        Assert.Equal("""["draft-1"]""", (string?)body["arguments"]);
        Assert.Equal("Basic", ask.Authorization?[..5]);
        // the console writes asked and the answer itself: only what ran is posted, so the record has no duplicate
        Assert.Equal(["tool-called"], Events(console).Select(e => (string?)e["kind"]));
    }

    [Fact]
    public async Task A_denial_in_the_inbox_ends_the_run_and_nothing_runs()
    {
        var console = new FakeConsole { Answers = ["denied"] };

        var (lines, product, _) = await Run(console, false, Call(10, "workspace_edit", "a"), Call(11, "workspace_show"));

        Assert.Empty(product.Ran);
        Assert.True(IsError(lines, 10));
        Assert.True(IsError(lines, 11));
        Assert.Equal(["tool-refused"], Events(console).Select(e => (string?)e["kind"]));
    }

    [Fact]
    public async Task A_console_that_is_down_never_lets_a_change_run()
    {
        var console = new FakeConsole { Down = true };

        var (lines, product, log) = await Run(console, false, Call(10, "workspace_edit", "a"));

        Assert.Empty(product.Ran);
        Assert.True(IsError(lines, 10));
        Assert.Contains("console", (string)lines.Single(l => l["id"] is JsonValue v && v.TryGetValue<int>(out var i) && i == 10)["result"]!["content"]![0]!["text"]!.ToString());
        Assert.Contains("did not take the record", log.ToString());
    }

    [Fact]
    public async Task A_client_that_asks_the_person_itself_has_the_ask_and_the_answer_reported_with_what_ran()
    {
        var console = new FakeConsole();
        var answer = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "approval-1", ["result"] = new JsonObject { ["action"] = "accept", ["content"] = new JsonObject { ["approve"] = true } } }.ToJsonString();

        var (lines, product, _) = await Run(console, true, Call(10, "workspace_edit", "a"), answer);

        Assert.False(IsError(lines, 10));
        Assert.Equal(["edit a"], product.Ran);
        Assert.DoesNotContain(console.Requests, r => r.Path.EndsWith("/outside/ask", StringComparison.Ordinal));
        Assert.Equal(["approval-asked", "approval-approved", "tool-called"], Events(console).Select(e => (string?)e["kind"]));
    }

    [Theory]
    [InlineData("scripted-agent 0.3", "scripted-agent", "0.3")]
    [InlineData("an unnamed agent", "an unnamed agent", "")]
    [InlineData("Claude Code 2.1.4", "Claude Code", "2.1.4")]
    public void The_clients_name_and_version_are_told_apart(string text, string name, string version) =>
        Assert.Equal((name, version), ConsoleReporter.SplitAgent(text));
}
