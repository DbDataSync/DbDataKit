using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Mcp;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Model Context Protocol server: what it offers a model, what it withholds, and that a tool is the command's own JSON document.</summary>
public class McpServerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-mcp-" + Guid.NewGuid().ToString("N")[..8]);

    public McpServerTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private McpServer Server(bool allowWrites = false) => new(dir, allowWrites, TextReader.Null, new StringWriter(), new TuiCommand.CliHost(_ => null));

    private static JsonObject Request(string method, JsonObject? p = null, int id = 1) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static JsonObject Result(McpServer s, string method, JsonObject? p = null) =>
        (JsonObject)s.Handle(Request(method, p)).Single()["result"]!;

    private static JsonObject Call(McpServer s, string tool, JsonObject? arguments = null) =>
        Result(s, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });

    [Fact]
    public void Initialize_negotiates_a_protocol_version_and_names_the_server()
    {
        var r = Result(Server(), "initialize", new JsonObject { ["protocolVersion"] = "2025-03-26" });
        Assert.Equal("2025-03-26", (string)r["protocolVersion"]!);
        Assert.Equal("dbdatabuild", (string)r["serverInfo"]!["name"]!);
        Assert.Equal(McpServer.LatestProtocol, (string)Result(Server(), "initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" })["protocolVersion"]!);
    }

    [Fact]
    public void Commands_that_change_a_target_are_not_offered_unless_the_operator_allowed_them()
    {
        var names = Server().ToolNames;
        foreach (var write in new[] { "connection_refresh", "connection_seed", "connection_init", "connection_publish" }) Assert.DoesNotContain(write, names);
        foreach (var read in new[] { "project_compile", "connection_deploy", "project_show_graph", "project_show_metadata", "project_tests_run", "help_code", "connection_compare" }) Assert.Contains(read, names);
        Assert.DoesNotContain("ui_terminal", names);
        Assert.DoesNotContain("ui_mcp", names);
        Assert.Contains("connection_refresh", Server(allowWrites: true).ToolNames);

        // deploy is offered to plan; applying a plan and recording a decision are options only --allow-writes brings
        string[] Inputs(McpServer s) => ((JsonObject)Result(s, "tools/list")["tools"]!.AsArray().Single(t => (string)t!["name"]! == "connection_deploy")!["inputSchema"]!["properties"]!).Select(p => p.Key).ToArray();
        Assert.Contains("write_plan", Inputs(Server()));
        foreach (var withheld in new[] { "apply_plan", "yes", "ack" }) Assert.DoesNotContain(withheld, Inputs(Server()));
        foreach (var offered in new[] { "apply_plan", "yes", "ack" }) Assert.Contains(offered, Inputs(Server(allowWrites: true)));
    }

    [Fact]
    public void The_model_is_never_offered_a_way_to_see_values_or_to_leave_the_project()
    {
        var tools = Result(Server(), "tools/list")["tools"]!.AsArray();
        foreach (var tool in tools)
        {
            var properties = ((JsonObject)tool!["inputSchema"]!["properties"]!).Select(p => p.Key).ToList();
            Assert.DoesNotContain("project", properties);
            Assert.DoesNotContain("show_values", properties);
            if ((string)tool["name"]! == "project_sample") Assert.DoesNotContain("data", properties);
        }
        Assert.True((bool)tools.Single(t => (string)t!["name"]! == "project_tests_run")!["annotations"]!["readOnlyHint"]!);
        Assert.False((bool)tools.Single(t => (string)t!["name"]! == "project_compile")!["annotations"]!["readOnlyHint"]!);       // it writes the compiled files
    }

    [Fact]
    public void A_tool_returns_the_commands_own_document_and_a_finding_is_not_an_error_of_the_call()
    {
        var r = Call(Server(), "project_tests_run");
        Assert.False((bool)r["isError"]!);
        Assert.Equal("project tests run", (string)r["structuredContent"]!["command"]!);
        Assert.Equal("project tests run", (string)JsonNode.Parse((string)r["content"]![0]!["text"]!)!["command"]!);
    }

    [Fact]
    public void A_path_outside_the_project_an_unknown_input_and_a_withheld_option_are_refused_without_running_anything()
    {
        var s = Server();
        foreach (var arguments in new[]
        {
            new JsonObject { ["models"] = new JsonArray("../other") },
            new JsonObject { ["models"] = new JsonArray("/etc/passwd") },
            new JsonObject { ["nonsense"] = true },
            new JsonObject { ["project"] = "/tmp" },
        })
        {
            var r = Call(s, "project_compile", arguments);
            Assert.True((bool)r["isError"]!);
        }
        Assert.True((bool)Call(s, "connection_compare", new JsonObject { ["show_values"] = true })["isError"]!);
        Assert.True((bool)Call(s, "project_model_update", new JsonObject { ["answers"] = "../../secrets.yml" })["isError"]!);
    }

    [Fact]
    public void An_unknown_tool_and_an_unknown_method_are_protocol_errors()
    {
        var s = Server();
        Assert.Equal(-32602, (int)s.Handle(Request("tools/call", new JsonObject { ["name"] = "connection_refresh" })).Single()["error"]!["code"]!);       // not offered
        Assert.Equal(-32601, (int)s.Handle(Request("no/such")).Single()["error"]!["code"]!);
        Assert.Empty(s.Handle(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));      // a notification is not answered
    }

    [Fact]
    public void The_skill_the_schemas_and_every_diagnostic_explanation_are_resources()
    {
        var s = Server();
        var listed = Result(s, "resources/list")["resources"]!.AsArray().Select(r => (string)r!["uri"]!).ToList();
        Assert.Contains("dbdatabuild://skill", listed);
        Assert.Contains("dbdatabuild://schemas/output.schema.json", listed);
        Assert.Contains("dbdatabuild", (string)Result(s, "resources/read", new JsonObject { ["uri"] = "dbdatabuild://skill" })["contents"]![0]!["text"]!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DDB-214", (string)Result(s, "resources/read", new JsonObject { ["uri"] = "dbdatabuild://explain/DDB-214" })["contents"]![0]!["text"]!);
        Assert.Equal(-32002, (int)s.Handle(Request("resources/read", new JsonObject { ["uri"] = "dbdatabuild://explain/DDB-0" })).Single()["error"]!["code"]!);
    }

    [Fact]
    public void The_workflows_of_the_skill_are_prompts_that_name_tools_the_server_really_has()
    {
        var s = Server();
        var listed = Result(s, "prompts/list")["prompts"]!.AsArray();
        Assert.Contains("add-model", listed.Select(p => (string)p!["name"]!));
        var text = (string)Result(s, "prompts/get", new JsonObject { ["name"] = "add-model", ["arguments"] = new JsonObject { ["name"] = "marts.fct_x", ["purpose"] = "one row per x" } })["messages"]![0]!["content"]!["text"]!;
        Assert.Contains("marts.fct_x", text);
        Assert.Contains("dbdatabuild://skill", text);
        // every command a prompt tells the model to call exists as a tool here, and none is a command the server withholds
        foreach (var prompt in listed)
        {
            var args = new JsonObject();
            foreach (var a in prompt!["arguments"]!.AsArray()) args[(string)a!["name"]!] = "x.y";
            var body = (string)Result(s, "prompts/get", new JsonObject { ["name"] = (string)prompt["name"]!, ["arguments"] = args })["messages"]![0]!["content"]!["text"]!;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"`((?:project|connection|help|ui)_[a-z_]+)`"))
                Assert.Contains(m.Groups[1].Value, s.ToolNames);
        }
        Assert.Equal(-32602, (int)s.Handle(Request("prompts/get", new JsonObject { ["name"] = "add-model" })).Single()["error"]!["code"]!);       // a required argument is missing
        Assert.Equal(-32602, (int)s.Handle(Request("prompts/get", new JsonObject { ["name"] = "nope" })).Single()["error"]!["code"]!);
    }

    /// <summary>A command that runs until it is asked to stop.</summary>
    private sealed class SlowHost : DbDataBuild.Tui.Model.ICommandHost
    {
        public readonly ManualResetEventSlim Started = new();
        public bool Stopped;
        public IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> Commands { get; } = [new("slow", "Takes a while", "Offline only", DbDataBuild.Tui.Model.Impact.None, [], [])];
        public DbDataBuild.Tui.Model.CommandResult Run(IReadOnlyList<string> args, DbDataBuild.Tui.Model.RunHooks? hooks = null)
        {
            Started.Set();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until) { if (hooks?.StopRequested?.Invoke() == true) { Stopped = true; break; } Thread.Sleep(10); }
            return new(0, "{}", "");
        }
    }

    /// <summary>Lines the test hands to the server one at a time, as a client would.</summary>
    private sealed class Feed : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string> lines = new();
        public void Send(string line) => lines.Add(line);
        public void End() => lines.CompleteAdding();
        public override string? ReadLine() => lines.TryTake(out var l, Timeout.Infinite) ? l : null;
    }

    [Fact]
    public void A_cancelled_request_stops_its_command_and_gets_no_reply_while_the_server_goes_on()
    {
        var host = new SlowHost();
        var feed = new Feed();
        var output = new StringWriter();
        var server = new McpServer(dir, false, feed, output, host);
        var serving = Task.Run(server.Serve);
        feed.Send(Request("tools/call", new JsonObject { ["name"] = "slow" }, id: 7).ToJsonString());
        Assert.True(host.Started.Wait(TimeSpan.FromSeconds(10)));
        feed.Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 7 } }.ToJsonString());
        feed.Send(Request("ping", id: 8).ToJsonString());
        feed.End();
        Assert.True(serving.Wait(TimeSpan.FromSeconds(15)));
        Assert.True(host.Stopped);
        var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal([8], replies.Select(r => (int)r["id"]!));        // nothing for 7, an answer for 8
    }

    /// <summary>A writer the test can read while the server writes to it.</summary>
    private sealed class SyncWriter : StringWriter
    {
        public override void WriteLine(string? value) { lock (this) base.WriteLine(value); }
        public override string ToString() { lock (this) return base.ToString(); }
    }

    private sealed class ApplyHost : DbDataBuild.Tui.Model.ICommandHost
    {
        public readonly List<List<string>> Ran = [];
        public IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> Commands { get; } =
        [
            new("connection deploy", "Deploy", "Target writes", DbDataBuild.Tui.Model.Impact.Target,
                [],
                [new("--apply-plan", "The plan", DbDataBuild.Tui.Model.OptionKind.Path, null, []), new("--write-plan", "Plan only", DbDataBuild.Tui.Model.OptionKind.Flag, null, []), new("--dry-run", "Check only", DbDataBuild.Tui.Model.OptionKind.Flag, null, []), new("--allow-risky", "Allow risky steps", DbDataBuild.Tui.Model.OptionKind.Flag, null, []), new("--project", "Project", DbDataBuild.Tui.Model.OptionKind.Path, null, [])], "--apply-plan|--yes|--ack"),
            new("project compile", "Compile", "Offline only", DbDataBuild.Tui.Model.Impact.None, [], [new("--project", "Project", DbDataBuild.Tui.Model.OptionKind.Path, null, [])]),
        ];
        public DbDataBuild.Tui.Model.CommandResult Run(IReadOnlyList<string> args, DbDataBuild.Tui.Model.RunHooks? hooks = null) { lock (Ran) Ran.Add([.. args]); return new(0, "{\"ok\":true}", ""); }
    }

    private static string WaitForLine(SyncWriter output, string contains)
    {
        for (var i = 0; i < 400; i++)
        {
            var line = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(l => l.Contains(contains));
            if (line != null) return line;
            Thread.Sleep(25);
        }
        throw new TimeoutException($"The server never wrote a line with `{contains}`.");
    }

    /// <summary>Runs a session: initialize (with or without elicitation), then a call of apply; `answer` gets the question the server asks and returns what the person answered (null: nothing asked is expected).</summary>
    private (List<JsonNode> Replies, ApplyHost Host) ApplySession(bool clientAsks, Func<JsonNode, JsonObject>? answer, bool dryRun = false)
    {
        var plan = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(plan);
        var id = "2026-10-05-eeee0005";
        File.WriteAllText(Path.Combine(plan, id + ".plan.yml"), DbDataBuild.Planning.PlanDocument.Serialize(new DbDataBuild.Planning.Plan(id, "postgres", null, false, "0.1.0",
            [new DbDataBuild.Planning.ObjectBase("marts.fct", DbDataBuild.State.ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
            [new DbDataBuild.Planning.PlanStep("1", DbDataBuild.Planning.StepType.Ddl, "marts.fct", "drop column x", "ALTER TABLE marts.fct DROP COLUMN x;", DbDataBuild.Planning.RiskClass.Destructive, ["col.dropped"], new string('b', 64), [])], [])));
        var host = new ApplyHost(); var feed = new Feed(); var output = new SyncWriter();
        var server = new McpServer(dir, true, feed, output, host) { AnswerTimeout = TimeSpan.FromSeconds(20) };
        var serving = Task.Run(server.Serve);
        feed.Send(Request("initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = clientAsks ? new JsonObject { ["elicitation"] = new JsonObject() } : new JsonObject() }, id: 1).ToJsonString());
        var arguments = new JsonObject { ["apply_plan"] = $"plans/postgres/{id}.plan.yml" };
        if (dryRun) arguments["dry_run"] = true;
        feed.Send(Request("tools/call", new JsonObject { ["name"] = "connection_deploy", ["arguments"] = arguments }, id: 2).ToJsonString());
        if (answer != null)
        {
            var asked = JsonNode.Parse(WaitForLine(output, "elicitation/create"))!;
            Assert.Contains("drop column x", (string)asked["params"]!["message"]!);          // the person is shown what the plan does
            feed.Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = asked["id"]!.DeepClone(), ["result"] = answer(asked) }.ToJsonString());
        }
        WaitForLine(output, "\"id\":2");
        feed.End();
        Assert.True(serving.Wait(TimeSpan.FromSeconds(15)));
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList(), host);
    }

    private static JsonObject Accept(bool approve, string typed) => new() { ["action"] = "accept", ["content"] = new JsonObject { ["approve"] = approve, ["type_to_confirm"] = typed } };

    [Fact]
    public void A_command_that_changes_a_database_runs_only_when_the_person_approves_through_the_host()
    {
        var (replies, host) = ApplySession(clientAsks: true, _ => Accept(true, "connection deploy"));
        Assert.False((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        var ran = Assert.Single(host.Ran);
        Assert.Equal(["connection", "deploy", "--apply-plan"], ran.Take(3));
        Assert.EndsWith("eeee0005.plan.yml", ran[3]);
        Assert.True(Path.IsPathRooted(ran[3]));          // the plan is found from the project, not from wherever the server was started
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("not approved")]
    [InlineData("wrong text")]
    public void Anything_but_a_clear_approval_runs_nothing(string how)
    {
        var (replies, host) = ApplySession(clientAsks: true, _ => how switch
        {
            "decline" => new JsonObject { ["action"] = "decline" },
            "cancel" => new JsonObject { ["action"] = "cancel" },
            "not approved" => Accept(false, "connection deploy"),
            _ => Accept(true, "yes please"),
        });
        Assert.True((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public void A_host_that_cannot_ask_gets_a_refusal_that_says_what_to_run_by_hand()
    {
        var (replies, host) = ApplySession(clientAsks: false, answer: null);
        var result = replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!;
        Assert.True((bool)result["isError"]!);
        Assert.Contains("Run it yourself: dbdatabuild connection deploy --apply-plan", (string)result["content"]![0]!["text"]!);
        Assert.Empty(host.Ran);
    }

    [Fact]
    public void A_dry_run_changes_nothing_so_it_needs_no_approval()
    {
        var (replies, host) = ApplySession(clientAsks: false, answer: null, dryRun: true);
        Assert.False((bool)replies.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["isError"]!);
        Assert.Contains("--dry-run", Assert.Single(host.Ran));
    }

    // ---- the app ----

    private static JsonObject UiClient() => new() { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = new JsonObject { ["extensions"] = new JsonObject { [McpServer.UiExtension] = new JsonObject { ["mimeTypes"] = new JsonArray(McpServer.UiMime) } } } };

    private McpServer UiServer(DbDataBuild.Tui.Model.ICommandHost? host = null, bool allowApply = false)
    {
        var server = new McpServer(dir, false, TextReader.Null, new StringWriter(), host ?? new TuiCommand.CliHost(_ => null), allowApply);
        Result(server, "initialize", UiClient());
        return server;
    }

    private static string Text(JsonObject result) => (string)result["content"]![0]!["text"]!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_show_removes_the_tool_whether_or_not_the_host_has_the_app(bool hostHasApp)
    {
        var s = new McpServer(dir, false, TextReader.Null, new StringWriter(), new TuiCommand.CliHost(_ => null), noShow: true);
        var caps = hostHasApp ? new JsonObject { ["extensions"] = new JsonObject { [McpServer.UiExtension] = new JsonObject() } } : new JsonObject();
        Result(s, "initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = caps });
        Assert.DoesNotContain(Result(s, "tools/list")["tools"]!.AsArray(), t => (string)t!["name"]! == "show");
        Assert.Equal(-32602, (int)s.Handle(Request("tools/call", new JsonObject { ["name"] = "show", ["arguments"] = new JsonObject() })).Single()["error"]!["code"]!);
    }

    [Fact]
    public void No_tool_carries_a_null_member_because_a_strict_client_rejects_the_whole_list_over_one()
    {
        foreach (var hostHasApp in new[] { false, true })
        {
            var s = Server();
            var caps = hostHasApp ? new JsonObject { ["extensions"] = new JsonObject { [McpServer.UiExtension] = new JsonObject() } } : new JsonObject();
            Result(s, "initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = caps });
            foreach (var tool in Result(s, "tools/list")["tools"]!.AsArray())
                foreach (var member in (JsonObject)tool!) Assert.True(member.Value != null, $"{tool["name"]}.{member.Key} is null (host has app: {hostHasApp})");
        }
    }

    [Fact]
    public void Show_in_a_host_without_the_app_gives_a_link_that_works_once()
    {
        var s = Server();
        Result(s, "initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = new JsonObject() });
        try
        {
            var r = Call(s, "show", new JsonObject { ["screen"] = "plans" });
            Assert.False((bool)r["isError"]!);
            var url = (string)r["structuredContent"]!["url"]!;
            Assert.Matches(@"^http://127\.0\.0\.1:\d+/\?token=[0-9a-f]{48}&screen=plans$", url);
            Assert.True((bool)r["structuredContent"]!["single_use"]!);
            Assert.Contains("Do not open it yourself", Text(r));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            Assert.Equal(System.Net.HttpStatusCode.Redirect, http.GetAsync(url).GetAwaiter().GetResult().StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, http.GetAsync(url).GetAwaiter().GetResult().StatusCode);      // spent
            var second = (string)Call(s, "show")["structuredContent"]!["url"]!;                                              // another link, the same server
            Assert.NotEqual(url, second);
            Assert.Equal(new Uri(url).Port, new Uri(second).Port);
        }
        finally { s.DisposePage(); }
    }

    [Fact]
    public void A_host_without_the_app_extension_sees_no_app_no_app_only_tool_and_cannot_call_one()
    {
        var s = Server();
        Result(s, "initialize", new JsonObject { ["protocolVersion"] = McpServer.LatestProtocol, ["capabilities"] = new JsonObject() });
        var tools = Result(s, "tools/list")["tools"]!.AsArray();
        Assert.DoesNotContain(tools, t => ((string)t!["name"]!).StartsWith("ui_"));
        Assert.Contains("a link", (string)tools.Single(t => (string)t!["name"]! == "show")!["description"]!);         // `show` is there, as a link to the page in a browser
        Assert.All(tools, t => Assert.Null(t!["_meta"]));
        Assert.DoesNotContain("ui://dbdatabuild/app", Result(s, "resources/list")["resources"]!.AsArray().Select(r => (string)r!["uri"]!));
        Assert.Equal(-32602, (int)s.Handle(Request("tools/call", new JsonObject { ["name"] = "ui_run", ["arguments"] = new JsonObject { ["command"] = "project_compile" } })).Single()["error"]!["code"]!);
        Assert.Equal(-32002, (int)s.Handle(Request("resources/read", new JsonObject { ["uri"] = "ui://dbdatabuild/app" })).Single()["error"]!["code"]!);
    }

    [Fact]
    public void A_host_with_the_extension_gets_the_app_its_tools_and_the_ones_only_the_app_may_call()
    {
        var s = UiServer();
        var tools = Result(s, "tools/list")["tools"]!.AsArray();
        foreach (var name in new[] { "ui_run", "ui_file", "ui_capabilities", "ui_answers", "ui_apply", "ui_job", "ui_stop" })
            Assert.Equal(["app"], tools.Single(t => (string)t!["name"]! == name)!["_meta"]!["ui"]!["visibility"]!.AsArray().Select(v => (string)v!));      // not offered to the model
        foreach (var name in new[] { "project_show_plan", "connection_deploy", "project_show_graph", "connection_compare", "project_sample", "show" })
            Assert.Equal("ui://dbdatabuild/app", (string)tools.Single(t => (string)t!["name"]! == name)!["_meta"]!["ui"]!["resourceUri"]!);
        Assert.Null(tools.Single(t => (string)t!["name"]! == "project_compile")!["_meta"]);          // the model's own checks do not open a window each time
        var resource = Result(s, "resources/read", new JsonObject { ["uri"] = "ui://dbdatabuild/app" })["contents"]![0]!;
        Assert.Equal("text/html;profile=mcp-app", (string)resource["mimeType"]!);
        var html = (string)resource["text"]!;
        Assert.Contains("const TRANSPORT = \"mcp\"", html);
        Assert.DoesNotContain("__TRANSPORT__", html);
        Assert.DoesNotContain("__TOKEN__", html);
        Assert.Contains("ui://dbdatabuild/app", Result(s, "resources/list")["resources"]!.AsArray().Select(r => (string)r!["uri"]!));
    }

    [Fact]
    public void The_app_runs_what_the_page_runs_and_reads_what_the_page_reads_and_no_more()
    {
        var s = UiServer();
        var validate = Call(s, "ui_run", new JsonObject { ["command"] = "project_compile" });
        Assert.False((bool)validate["isError"]!);
        Assert.Equal("project compile", (string)JsonNode.Parse(Text(validate))!["document"]!["command"]!);
        Assert.True((bool)Call(s, "ui_run", new JsonObject { ["command"] = "connection_refresh" })["isError"]!);                       // not a command the page runs
        Assert.True((bool)Call(s, "ui_run", new JsonObject { ["command"] = "project_compile", ["arguments"] = new JsonObject { ["write"] = true } })["isError"]!);
        File.WriteAllText(Path.Combine(dir, ".env"), "SECRET=1");
        Assert.True((bool)Call(s, "ui_file", new JsonObject { ["path"] = ".env" })["isError"]!);
        Assert.True((bool)Call(s, "ui_file", new JsonObject { ["path"] = "../x.sql" })["isError"]!);
        var yml = Call(s, "ui_file", new JsonObject { ["path"] = "dbdatabuild.yml" });
        Assert.False((bool)yml["isError"]!);
        Assert.Contains("connections", (string)JsonNode.Parse(Text(yml))!["text"]!, StringComparison.OrdinalIgnoreCase);
        Assert.False((bool)JsonNode.Parse(Text(Call(s, "ui_capabilities")))!["apply"]!);
    }

    [Fact]
    public void Show_names_the_screen_the_person_should_see()
    {
        var r = Call(UiServer(), "show", new JsonObject { ["screen"] = "plans" });
        Assert.Equal("show", (string)r["structuredContent"]!["command"]!);
        Assert.Equal("plans", (string)r["structuredContent"]!["screen"]!);
        Assert.Equal("health", (string)Call(UiServer(), "show", new JsonObject { ["screen"] = "nonsense" })["structuredContent"]!["screen"]!);
    }

    [Fact]
    public void An_app_applies_a_plan_only_with_allow_apply_and_the_persons_confirmation_and_the_model_is_never_offered_that()
    {
        var host = new ApplyHost();
        var plan = Path.Combine(dir, "plans", "postgres");
        Directory.CreateDirectory(plan);
        var id = "2026-10-05-eeee0005";
        File.WriteAllText(Path.Combine(plan, id + ".plan.yml"), DbDataBuild.Planning.PlanDocument.Serialize(new DbDataBuild.Planning.Plan(id, "postgres", null, false, "0.1.0",
            [new DbDataBuild.Planning.ObjectBase("marts.fct", DbDataBuild.State.ObjectState.InSync, new string('a', 64), new string('a', 64))], [],
            [new DbDataBuild.Planning.PlanStep("1", DbDataBuild.Planning.StepType.Ddl, "marts.fct", "add column", "ALTER TABLE marts.fct ADD x int;", DbDataBuild.Planning.RiskClass.Safe, ["col.added"], new string('b', 64), [])], [])));
        var relative = $"plans/postgres/{id}.plan.yml";
        var confirm = new JsonObject { ["connection"] = "postgres", ["plan_id"] = id };

        // the server was not started with --allow-apply: refused, nothing runs
        var off = UiServer(host, allowApply: false);
        Assert.True((bool)Call(off, "ui_apply", new JsonObject { ["plan"] = relative, ["confirm"] = confirm.DeepClone() })["isError"]!);
        Assert.Empty(host.Ran);

        var on = UiServer(host, allowApply: true);
        Assert.True((bool)Call(on, "ui_apply", new JsonObject { ["plan"] = relative, ["confirm"] = new JsonObject { ["connection"] = "nope", ["plan_id"] = id } })["isError"]!);       // not confirmed
        Assert.Empty(host.Ran);
        var started = Call(on, "ui_apply", new JsonObject { ["plan"] = relative, ["confirm"] = confirm.DeepClone() });
        Assert.False((bool)started["isError"]!);
        var job = (string)JsonNode.Parse(Text(started))!["job"]!;
        for (var i = 0; i < 400 && !(bool)JsonNode.Parse(Text(Call(on, "ui_job", new JsonObject { ["id"] = job })))!["done"]!; i++) Thread.Sleep(25);
        Assert.Equal(["connection", "deploy", "--apply-plan"], Assert.Single(host.Ran).Take(3));

        // and the model, which sees none of these tools, has no apply: it is a command only --allow-writes offers, and then only with the person's approval through the host
        Assert.DoesNotContain("connection_refresh", on.ToolNames);
    }

    [Fact]
    public void Serve_answers_each_line_and_survives_a_line_that_is_not_json()
    {
        var output = new StringWriter();
        var input = new StringReader(Request("ping").ToJsonString() + "\nthis is not json\n\n" + Request("tools/list", id: 2).ToJsonString() + "\n");
        new McpServer(dir, false, input, output, new TuiCommand.CliHost(_ => null)).Serve();
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(-32700, (int)lines[1]["error"]!["code"]!);
        Assert.NotNull(lines[2]["result"]!["tools"]);
    }

    [Fact]
    public void The_command_refuses_a_json_format_and_a_missing_project()
    {
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "mcp", "--project", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        var (o, e) = (new StringWriter(), new StringWriter());
        Assert.Equal(CliApp.ExitUsage, CliApp.Run(["ui", "mcp", "--project", Path.Combine(dir, "nope")], o, e));
        Assert.Equal("", o.ToString());      // nothing but protocol ever goes to standard output
    }
}
