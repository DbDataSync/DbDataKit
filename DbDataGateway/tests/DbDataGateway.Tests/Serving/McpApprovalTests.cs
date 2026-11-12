using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Tests.Config;
using DbDataGateway.Tests.Support;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 20 O12: the approvals of the <c>mcp</c> module (a step with an effect waits for a person, runs once when one approves, never when denied or expired), and the protocol in front of them, with a tool that counts its runs.</summary>
public sealed class McpApprovalTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbgateway-mcp-").FullName;
    private readonly StateDatabase _state;
    private readonly StoreCallers _callers;
    private int _runs;
    private string? _ranAs;

    public McpApprovalTests()
    {
        _state = GatewayState.Open(_dir);
        _callers = new StoreCallers(_state, "srv", null);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(_dir);
    }

    private StoreCaller Person(string name, ServerRole roles)
    {
        var (user, token) = HomeSeed.Person(_state, name, $"{name.ToLowerInvariant()}@example.invalid");
        new HomeDirectory(_state).Assign(AssignmentSubject.User(user.Id), "srv", roles);
        return _callers.Authenticate("Bearer " + token)! with { Interactive = true };
    }

    private StoreCaller Agent(string name, ServerRole roles)
    {
        var token = HomeSeed.ServiceAccount(_state, name);
        var caller = _callers.Authenticate("Bearer " + token)!;
        new HomeDirectory(_state).Assign(AssignmentSubject.User(caller.UserId), "srv", roles);
        return _callers.Authenticate("Bearer " + token)!;
    }

    private McpTool Effect() => new("touch", "Changes something.", new JsonObject { ["type"] = "object" }, true, call =>
    {
        Interlocked.Increment(ref _runs);
        _ranAs = call.Caller.UserId;
        return Task.FromResult<JsonNode>(new JsonObject { ["touched"] = call.Arguments["what"]?.ToString() });
    }, Summary: a => $"Touch {a["what"]}.", Check: (caller, _) => caller.Holds(ServerRole.Author) || caller.Holds(ServerRole.Admin) ? null : "needs author");

    private McpTool Read() => new("look", "Reads something.", new JsonObject { ["type"] = "object" }, false, call => Task.FromResult<JsonNode>(new JsonObject { ["seen_by"] = call.Caller.UserId }));

    private (McpServer Server, McpApprovals Approvals) Make(TimeSpan? window = null)
    {
        IReadOnlyList<McpTool> tools = [Effect(), Read()];
        var approvals = new McpApprovals(_state, () => _callers, tools.ToDictionary(t => t.Name), window ?? TimeSpan.FromHours(1));
        return (new McpServer(tools, approvals, "test"), approvals);
    }

    private static async Task<JsonObject> CallAsync(McpServer server, StoreCaller caller, string tool, JsonObject? arguments = null)
    {
        var answer = (JsonObject)(await server.HandleAsync(caller, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] } }, default))!;
        return (JsonObject)answer["result"]!;
    }

    private static string Text(JsonObject result) => (string)result["content"]![0]!["text"]!;

    private static Guid PendingId(JsonObject result)
    {
        var text = Text(result);
        Assert.StartsWith("pending approval ", text);
        return Guid.Parse(text["pending approval ".Length..]);
    }

    [Fact]
    public async Task An_agents_write_waits_for_approval_and_does_not_run()
    {
        var (server, approvals) = Make();
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);

        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "territory NO" }));

        Assert.Equal(0, _runs);
        var row = approvals.Get(id)!;
        Assert.Equal("pending", row.Status);
        Assert.Equal(agent.UserId, row.Person);
        Assert.Equal("touch", row.Tool);
        Assert.Equal("Touch territory NO.", row.Preview);
        Assert.Contains("territory NO", row.Arguments);
        Assert.Equal("pending", (string)(await CallAsync(server, agent, "approval_status", new() { ["id"] = id.ToString() }))["structuredContent"]!["status"]!);
        Assert.Equal(0, _runs);
        // the request is in the audit with who asked, the tool and the arguments
        var audit = new AgentStore(_state).Audit(origin: "mcp").Single();
        Assert.Equal("approval-asked", audit.Kind);
        Assert.Equal(agent.UserId, audit.Person);
        Assert.Equal("touch", audit.Tool);
    }

    [Fact]
    public async Task A_caller_who_could_not_do_it_anyway_is_refused_and_no_request_is_made()
    {
        var (server, approvals) = Make();
        var viewer = Person("Linus", ServerRole.Viewer);

        var refused = await CallAsync(server, viewer, "touch", new() { ["what"] = "x" });

        Assert.True((bool)refused["isError"]!);
        Assert.Empty(approvals.List(pendingOnly: false));
    }

    [Fact]
    public async Task A_persons_approval_runs_the_step_once_as_the_requester_and_the_agent_reads_what_it_gave()
    {
        var (server, approvals) = Make();
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);
        var approver = Person("Bea", ServerRole.Viewer | ServerRole.Approver);
        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "x" }));

        var first = await approvals.DecideAsync(approver, id, approve: true, "looks right", default);
        var second = await approvals.DecideAsync(approver, id, approve: true, null, default);

        Assert.Equal(200, first.Status);
        Assert.True((bool)first.Body["ran"]!);
        Assert.Equal(1, _runs);
        Assert.Equal(agent.UserId, _ranAs);
        Assert.Equal(409, second.Status);
        Assert.Equal(1, _runs);
        var status = (JsonObject)(await CallAsync(server, agent, "approval_status", new() { ["id"] = id.ToString() }))["structuredContent"]!;
        Assert.Equal("approved", (string)status["status"]!);
        Assert.True((bool)status["ran"]!);
        Assert.Equal("x", (string)status["result"]!["touched"]!);
        var row = approvals.Get(id)!;
        Assert.Equal(approver.UserId, row.AnsweredBy);
        // the audit names the person who asked, the agent and the one who approved
        var kinds = new AgentStore(_state).Audit(origin: "mcp").Select(a => a.Kind).Order().ToList();
        Assert.Equal(["approval-approved", "approval-asked", "tool-called"], kinds);
        Assert.Contains(new AgentStore(_state).Audit(origin: "mcp"), a => a.Kind == "approval-approved" && a.Detail!.Contains(approver.UserId));
    }

    [Fact]
    public async Task Two_approvals_at_once_run_the_step_once()
    {
        var (server, approvals) = Make();
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);
        var people = Enumerable.Range(0, 6).Select(i => Person("Approver" + i, ServerRole.Viewer | ServerRole.Approver)).ToList();
        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "x" }));

        var answers = await Task.WhenAll(people.Select(p => Task.Run(() => approvals.DecideAsync(p, id, approve: true, null, default))));

        Assert.Equal(1, answers.Count(a => a.Status == 200));
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task A_denied_step_never_runs()
    {
        var (server, approvals) = Make();
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);
        var approver = Person("Bea", ServerRole.Viewer | ServerRole.Approver);
        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "x" }));

        var denied = await approvals.DecideAsync(approver, id, approve: false, "not now", default);
        var later = await approvals.DecideAsync(approver, id, approve: true, null, default);

        Assert.Equal(200, denied.Status);
        Assert.Equal("denied", (string)denied.Body["status"]!);
        Assert.Equal(409, later.Status);
        Assert.Equal(0, _runs);
        Assert.Equal("denied", approvals.Get(id)!.Status);
        Assert.DoesNotContain(new AgentStore(_state).Audit(origin: "mcp"), a => a.Kind is "tool-called" or "tool-failed");
    }

    [Fact]
    public async Task An_expired_step_never_runs()
    {
        var (server, approvals) = Make(window: TimeSpan.FromMilliseconds(1));
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);
        var approver = Person("Bea", ServerRole.Viewer | ServerRole.Approver);
        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "x" }));
        await Task.Delay(100);

        var late = await approvals.DecideAsync(approver, id, approve: true, null, default);

        Assert.Equal(409, late.Status);
        Assert.Contains("expired", (string)late.Body["error"]!);
        Assert.Equal(0, _runs);
        Assert.Equal("expired", approvals.Get(id)!.Status);
        Assert.Equal("expired", (string)(await CallAsync(server, agent, "approval_status", new() { ["id"] = id.ToString() }))["structuredContent"]!["status"]!);
    }

    [Fact]
    public async Task Neither_an_agent_nor_the_requester_nor_a_person_without_the_role_can_approve()
    {
        var (server, approvals) = Make();
        var requester = Person("Ada", ServerRole.Viewer | ServerRole.Author | ServerRole.Approver);
        var otherAgent = Agent("helper", ServerRole.Viewer | ServerRole.Approver | ServerRole.Admin);
        var viewer = Person("Linus", ServerRole.Viewer);
        var id = PendingId(await CallAsync(server, requester, "touch", new() { ["what"] = "x" }));

        Assert.Equal(403, (await approvals.DecideAsync(requester, id, true, null, default)).Status);
        Assert.Equal(403, (await approvals.DecideAsync(otherAgent, id, true, null, default)).Status);
        Assert.Equal(403, (await approvals.DecideAsync(viewer, id, true, null, default)).Status);
        // a person's personal access token is a token an agent could hold: it does not answer
        var withToken = requester with { UserId = "someone-else", Interactive = false };
        Assert.Equal(403, (await approvals.DecideAsync(Person("Tok", ServerRole.Viewer | ServerRole.Approver) with { Interactive = false }, id, true, null, default)).Status);
        Assert.Contains("signed in", (string)(await approvals.DecideAsync(withToken, id, true, null, default)).Body["error"]!);
        Assert.Equal(404, (await approvals.DecideAsync(viewer, Guid.NewGuid(), true, null, default)).Status);
        Assert.Equal(0, _runs);
        Assert.Equal("pending", approvals.Get(id)!.Status);
        // an administrator who is another person may
        var admin = Person("Root", ServerRole.Viewer | ServerRole.Admin);
        Assert.Equal(200, (await approvals.DecideAsync(admin, id, true, null, default)).Status);
        Assert.Equal(1, _runs);
    }

    [Fact]
    public async Task An_agent_sees_only_its_own_requests_and_a_read_tool_runs_at_once_as_the_caller()
    {
        var (server, approvals) = Make();
        var agent = Agent("loader", ServerRole.Viewer | ServerRole.Author);
        var other = Agent("other", ServerRole.Viewer | ServerRole.Author);
        var id = PendingId(await CallAsync(server, agent, "touch", new() { ["what"] = "x" }));

        var theirs = await CallAsync(server, other, "approval_status", new() { ["id"] = id.ToString() });
        var read = await CallAsync(server, other, "look");

        Assert.Contains("no such approval", Text(theirs));
        Assert.Equal(other.UserId, (string)read["structuredContent"]!["seen_by"]!);
        Assert.Single(approvals.List(pendingOnly: true));
    }

    [Fact]
    public async Task The_protocol_answers_initialize_lists_tools_with_their_effect_and_refuses_an_unknown_method_and_tool()
    {
        var (server, _) = Make();
        var agent = Agent("loader", ServerRole.Viewer);
        var init = (JsonObject)(await server.HandleAsync(agent, JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""")!, default))!;
        var list = (JsonObject)(await server.HandleAsync(agent, JsonNode.Parse("""{"jsonrpc":"2.0","id":8,"method":"tools/list"}""")!, default))!;
        var unknown = (JsonObject)(await server.HandleAsync(agent, JsonNode.Parse("""{"jsonrpc":"2.0","id":9,"method":"nope"}""")!, default))!;
        var notification = await server.HandleAsync(agent, JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!, default);

        Assert.Equal("2025-03-26", (string)init["result"]!["protocolVersion"]!);
        var tools = ((JsonArray)list["result"]!["tools"]!).ToDictionary(t => (string)t!["name"]!, t => t!);
        Assert.False((bool)tools["look"]["annotations"]!["destructiveHint"]!);
        Assert.True((bool)tools["touch"]["annotations"]!["destructiveHint"]!);
        Assert.Contains("approval_status", tools.Keys);
        Assert.Equal(-32601, (int)unknown["error"]!["code"]!);
        Assert.Null(notification);
        Assert.True((bool)(await CallAsync(server, agent, "no_such_tool"))["isError"]!);
    }
}
