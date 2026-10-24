using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

public sealed class McpServerTests
{
    private static NounRouter Router(Func<NounRouter>? self = null)
    {
        NounRouter? router = null;
        router = new NounRouter("demo",
        [
            new Noun("thing", "Things",
            [
                new Verb("show", "Show a thing", EffectClass.OfflineOnly, "<name>", async (a, c) => { c.Data["name"] = a.FirstOrDefault(); await c.Out.WriteLineAsync($"thing {a.FirstOrDefault()}"); return 0; },
                    DataSchemas.Object([("name", DataSchemas.String)])),
                new Verb("break", "Fail", EffectClass.OfflineOnly, "", async (a, c) => { await c.Error.WriteLineAsync("broken"); return 1; }, DataSchemas.None),
                new Verb("write", "Change a thing", EffectClass.RepoFilesOnly, "<name>", (a, c) => Task.FromResult(0), DataSchemas.None),
            ]),
            new Noun("server", "Server", [new Verb("run", "Run", EffectClass.OfflineOnly, "", (a, c) => Task.FromResult(0), DataSchemas.None)]),
            UiNoun.Create(() => router!, "demo", "1.2.3", ["thing break"]),
        ], "1.2.3");
        return router;
    }

    private static McpServer Server(bool allowWrites = false, params string[] excluded) =>
        new(Router(), new McpOptions(new HashSet<string>(excluded), allowWrites), "demo", "1.2.3");

    private static async Task<JsonObject> Ask(McpServer server, string json) =>
        (JsonObject)(await server.HandleAsync(JsonNode.Parse(json)!)).Single();

    [Fact]
    public void Reading_commands_are_tools_and_the_server_itself_and_the_foreground_server_are_not()
    {
        Assert.Equal(["thing_show", "thing_break"], Server().Tools.Select(t => t.Name));
        Assert.Equal(["thing_show", "thing_break", "thing_write"], Server(allowWrites: true).Tools.Select(t => t.Name));
        Assert.Equal(["thing_show"], Server(excluded: "thing break").Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Initialize_names_the_server_and_agrees_a_protocol_version()
    {
        var reply = await Ask(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");
        Assert.Equal("2024-11-05", (string?)reply["result"]!["protocolVersion"]);
        Assert.Equal("demo", (string?)reply["result"]!["serverInfo"]!["name"]);
        var newer = await Ask(Server(), """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Assert.Equal(McpServer.LatestProtocol, (string?)newer["result"]!["protocolVersion"]);
    }

    [Fact]
    public async Task The_tool_list_describes_each_command_and_marks_the_ones_that_only_read()
    {
        var reply = await Ask(Server(allowWrites: true), """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = reply["result"]!["tools"]!.AsArray();
        var show = tools.Single(t => (string?)t!["name"] == "thing_show")!;
        Assert.Contains("Usage: thing show <name>", (string?)show["description"]);
        Assert.True((bool?)show["annotations"]!["readOnlyHint"]);
        Assert.False((bool?)tools.Single(t => (string?)t!["name"] == "thing_write")!["annotations"]!["readOnlyHint"]);
    }

    [Fact]
    public async Task A_call_runs_the_command_and_returns_its_document()
    {
        var reply = await Ask(Server(), """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"thing_show","arguments":{"args":["widget"]}}}""");
        Assert.False((bool?)reply["result"]!["isError"]);
        var document = JsonNode.Parse((string)reply["result"]!["content"]![0]!["text"]!)!;
        Assert.Equal("thing show", (string?)document["command"]);
        Assert.Equal("widget", (string?)document["data"]!["name"]);
    }

    [Fact]
    public async Task A_failing_command_is_an_error_result_not_a_protocol_error()
    {
        var reply = await Ask(Server(), """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"thing_break"}}""");
        Assert.True((bool?)reply["result"]!["isError"]);
        Assert.Contains("broken", (string)reply["result"]!["content"]![0]!["text"]!);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"thing_write"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"server_run"}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"thing_show","arguments":{"args":["--format","text"]}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"thing_show","arguments":{"args":[1]}}}""", -32602)]
    [InlineData("""{"jsonrpc":"2.0","id":5,"method":"nope"}""", -32601)]
    public async Task What_is_not_offered_or_not_valid_is_refused(string request, int code)
    {
        var reply = await Ask(Server(), request);
        Assert.Equal(code, (int?)reply["error"]!["code"]);
    }

    [Fact]
    public async Task A_notification_gets_no_answer_and_a_line_that_is_not_json_gets_a_parse_error()
    {
        var server = Server();
        Assert.Empty(await server.HandleAsync(JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!));
        using var output = new StringWriter();
        await server.ServeAsync(new StringReader("not json\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n"), output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(-32700, (int?)JsonNode.Parse(lines[0])!["error"]!["code"]);
        Assert.NotNull(JsonNode.Parse(lines[1])!["result"]);
    }

    [Fact]
    public async Task The_ui_mcp_verb_serves_the_router_it_is_part_of()
    {
        var router = Router();
        using var output = new StringWriter();
        var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n");
        var exit = await router.RunAsync(["ui", "mcp"], new CliContext(output, new StringWriter(), input));
        Assert.Equal(0, exit);
        Assert.Contains("thing_show", output.ToString());
        Assert.DoesNotContain("thing_break", output.ToString());   // excluded by the product
    }
}
