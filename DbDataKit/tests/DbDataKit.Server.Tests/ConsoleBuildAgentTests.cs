using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The build-helper agent and Build's commands through the relay as the person (plan 27a AG14): the agent is made from what a Build server offers an agent, its tools run on that server as the person who talks to it (the
/// console's own authority is never used), a command that writes files is offered only in a workspace and waits for the person, a command that reaches a database is never offered, and a denied step leaves the workspace as it was.
/// The Build server is a fake that records who called what; the real routes it stands for are tested in DbDataBuild's server tests.
/// </summary>
public sealed class ConsoleBuildAgentTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-build-agent-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private readonly FakeModels models = new();
    private readonly FakeBuild build = new();
    private string admin = "";
    private string adminId = "";
    private string other = "";

    private sealed class FakeModels : IAgentModels
    {
        public ScriptedChatClient? Client { get; set; }

        public BoundModel? For(string slot, string? channel) => Client is { } c ? new BoundModel(c, "scripted", new ModelPrice(1m, 2m), 100_000) : null;
    }

    /// <summary>A Build server as the console's relay reaches it: the calls are kept with the person they were made as.</summary>
    private sealed class FakeBuild : IBuildRelay
    {
        public List<(string Server, string PersonId, string Role, string Method, string Path, string? Body)> Calls { get; } = [];

        public Task<(int Status, JsonNode? Body)> CallAsync(string server, RelayPerson person, HttpMethod method, string path, object? body, CancellationToken cancellation)
        {
            Calls.Add((server, person.Id, person.Role.ToString(), method.Method, path, body is null ? null : System.Text.Json.JsonSerializer.Serialize(body)));
            if (path == "/api/v1/projects") return Ok(new JsonObject { ["projects"] = new JsonArray(new JsonObject { ["id"] = "sales" }) });
            if (path == "/api/v1/projects/sales/workspaces") return Ok(new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["id"] = "ws1", ["owned"] = true }) });
            if (path == "/api/v1/skill") return Ok(new JsonObject { ["name"] = "dbdatabuild", ["version"] = "9.9", ["content"] = "---\nname: dbdatabuild\ndescription: Work in a dbdatabuild project\n---\nCompile, then read.\n" });
            if (path == "/api/v1/projects/sales/commands")
                return Ok(new JsonObject
                {
                    ["commands"] = new JsonArray(
                        Command("project_show_graph", "project show graph", "Show the graph", "None", true),
                        Command("project_compile", "project compile", "Compile the models", "RepoFiles", true),
                        Command("connection_deploy", "connection deploy", "Plan or apply a deployment", "Target", true),
                        Command("connection_refresh", "connection refresh", "Refresh", "TargetData", true),
                        Command("project_publish", "project publish", "Send the project to a server", "None", true),
                        Command("project_seed", "project seed", "Seed", "RepoFiles", false)),
                });
            if (path.StartsWith("/api/v1/projects/sales/commands/project_show_graph", StringComparison.Ordinal)) return Ok(new JsonObject { ["exit"] = 0, ["document"] = new JsonObject { ["models"] = 3 } });
            if (path.StartsWith("/api/v1/projects/sales/commands/project_compile", StringComparison.Ordinal)) return Ok(new JsonObject { ["exit"] = 0, ["document"] = new JsonObject { ["compiled"] = true } });
            return Task.FromResult<(int, JsonNode?)>((404, new JsonObject { ["error"] = "no such call" }));
        }

        private static Task<(int, JsonNode?)> Ok(JsonNode body) => Task.FromResult<(int, JsonNode?)>((200, body));

        private static JsonObject Command(string tool, string name, string purpose, string impact, bool allowed) => new()
        {
            ["tool"] = tool, ["name"] = name, ["purpose"] = purpose, ["impact"] = impact, ["allowed"] = allowed,
            ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["models"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } }, ["required"] = new JsonArray(), ["additionalProperties"] = false },
        };
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"), configure: services =>
        {
            services.AddSingleton<IAgentModels>(models);
            services.AddSingleton<IBuildRelay>(build);
        });
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        (admin, adminId) = SignIn("Ada", UserRole.Admin);
        other = SignIn("Olga", UserRole.Admin).Session;
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private (string Session, string Id) SignIn(string name, UserRole role)
    {
        var user = app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role);
        return (app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id, user.Id);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    private async Task<JsonNode> Install()
    {
        var (status, body) = await Call(HttpMethod.Post, "/api/agents/build-helper/install", admin, new InstallBuildHelperBody("build-1", "sales"));
        Assert.Equal(HttpStatusCode.OK, status);
        return body!;
    }

    private async Task<string> StartConversation(string? workspace)
    {
        var (status, conversation) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("build-helper", "Models", "build-1", "sales", workspace));
        Assert.Equal(HttpStatusCode.OK, status);
        return (string)conversation!["id"]!;
    }

    private async Task<JsonNode?> PendingApproval()
    {
        for (var i = 0; i < 100; i++)
        {
            if ((await Call(HttpMethod.Get, "/api/agents/approvals", admin)).Body!.AsArray().FirstOrDefault() is { } found) return found;
            await Task.Delay(50);
        }
        return null;
    }

    [Fact]
    public async Task The_helper_is_made_from_what_the_server_offers_an_agent_and_its_skill_is_the_servers()
    {
        var detail = await Install();

        var yaml = await File.ReadAllTextAsync(Path.Combine(directory, "agents", "build-helper", "agent.yml"));
        Assert.Contains("- command: project compile", yaml);
        Assert.Contains("- command: project show graph", yaml);
        Assert.DoesNotContain("connection deploy", yaml);     // a command that reaches a database is never an agent's
        Assert.DoesNotContain("connection refresh", yaml);
        Assert.DoesNotContain("project publish", yaml);       // nor one that sends the project away
        Assert.Contains("Compile, then read.", await File.ReadAllTextAsync(Path.Combine(directory, "skills", "dbdatabuild", "SKILL.md")));
        Assert.Empty(detail["diagnostics"]!.AsArray());       // agent check reads it clean against what the server said
        var asks = detail["prompting"]!.AsArray().ToDictionary(t => (string)t!["tool"]!, t => (string)t!["effective"]!);
        Assert.Equal(("DoNotAsk", "AskOnce"), (asks["project_show_graph"], asks["project_compile"]));
        Assert.All(build.Calls, c => Assert.Equal((adminId, "Admin"), (c.PersonId, c.Role)));   // every call to the server was made as Ada
        Assert.Contains("agent-installed", (await Call(HttpMethod.Get, "/api/agents/audit", admin)).Body!.AsArray().Select(a => (string)a!["kind"]!));
    }

    [Fact]
    public async Task A_conversation_needs_a_target_and_a_write_is_offered_only_in_a_workspace()
    {
        await Install();
        models.Client = new ScriptedChatClient(ScriptStep.Say("ok"), ScriptStep.Say("ok"));

        var (status, refused) = await Call(HttpMethod.Post, "/api/agents/conversations", admin, new StartConversationBody("build-helper"));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("which server, project and workspace", (string)refused!["error"]!);

        var readOnly = await StartConversation(null);
        await Call(HttpMethod.Post, $"/api/agents/conversations/{readOnly}/messages", admin, new SendMessageBody("hi"));
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(readOnly);
        var inProject = models.Client.Options.Last()!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("project_show_graph", inProject);
        Assert.DoesNotContain("project_compile", inProject);   // no workspace: the files of the project are not an agent's to change

        var inWorkspace = await StartConversation("ws1");
        await Call(HttpMethod.Post, $"/api/agents/conversations/{inWorkspace}/messages", admin, new SendMessageBody("hi"));
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(inWorkspace);
        var offered = models.Client.Options.Last()!.Tools!.Select(t => t.Name).ToList();
        Assert.Contains("project_compile", offered);
        Assert.DoesNotContain(offered, n => n is "connection_deploy" or "connection_refresh" or "project_publish");
        Assert.DoesNotContain("project_seed", offered);        // the person may not run it on the server (author role), so it is not offered
    }

    [Fact]
    public async Task A_read_runs_on_the_server_as_the_person_in_their_workspace_and_a_write_waits_and_a_denied_one_changes_nothing()
    {
        await Install();
        models.Client = new ScriptedChatClient(
            ScriptStep.Call("project_show_graph", new { models = new[] { "orders" } }),
            ScriptStep.Call("project_compile", new { }),
            ScriptStep.Say("I could not compile."));
        var id = await StartConversation("ws1");
        build.Calls.Clear();

        await Call(HttpMethod.Post, $"/api/agents/conversations/{id}/messages", admin, new SendMessageBody("look at the graph and compile"));
        var pending = await PendingApproval();
        Assert.NotNull(pending);
        Assert.Equal(("project_compile", "reads-definitions, writes-definitions", "Ada"), ((string)pending["tool"]!, (string)pending["traits"]!, (string)pending["personName"]!));
        Assert.Equal(HttpStatusCode.Conflict, (await Call(HttpMethod.Post, $"/api/agents/approvals/{(string)pending["id"]!}", other, new AnswerBody(true))).Status);   // not another administrator's to give

        var read = Assert.Single(build.Calls);   // the read ran, the write has not
        Assert.Equal(("build-1", adminId, "POST", "/api/v1/projects/sales/commands/project_show_graph?workspace=ws1"), (read.Server, read.PersonId, read.Method, read.Path));
        Assert.Equal("""{"models":["orders"]}""", read.Body);   // only the arguments are sent: no login, no token of the console's own

        await Call(HttpMethod.Post, $"/api/agents/approvals/{(string)pending["id"]!}", admin, new AnswerBody(false, "not that"));
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(id);

        Assert.DoesNotContain(build.Calls, c => c.Path.Contains("project_compile"));       // a denied write leaves the workspace unchanged
        var (_, view) = await Call(HttpMethod.Get, $"/api/agents/conversations/{id}", admin);
        var audit = view!["audit"]!.AsArray();
        Assert.Contains(audit, a => (string)a!["kind"]! == "approval-denied");
        Assert.All(audit, a => Assert.Equal(("Ada", "build-helper"), ((string)a!["personName"]!, (string)a["agent"]!)));   // the audit names the person and the agent
    }

    [Fact]
    public async Task An_approved_write_runs_on_the_server_as_the_person_and_an_account_that_can_no_longer_sign_in_runs_nothing()
    {
        await Install();
        models.Client = new ScriptedChatClient(ScriptStep.Call("project_compile", new { }), ScriptStep.Say("done"));
        var id = await StartConversation("ws1");
        build.Calls.Clear();
        await Call(HttpMethod.Post, $"/api/agents/conversations/{id}/messages", admin, new SendMessageBody("compile"));
        var pending = await PendingApproval();

        app.Services.GetRequiredService<IUserAccounts>().SetEnabled(adminId, false);   // the person is switched off while the step waits
        Assert.True(app.Services.GetRequiredService<ConsoleAgents>().Answer(Guid.Parse((string)pending!["id"]!), true, adminId, null, anyPerson: false));   // answered by the desk: the switched-off person has no session left
        await app.Services.GetRequiredService<ConsoleAgents>().WhenIdle(id);

        Assert.DoesNotContain(build.Calls, c => c.Path.Contains("project_compile"));   // the agent never has more than its person: nothing ran as the console
    }

    [Fact]
    public async Task The_targets_are_listed_as_the_person_sees_them()
    {
        var (status, listing) = await Call(HttpMethod.Get, "/api/agents/build-targets?server=build-1&project=sales", admin);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sales", (string)listing!["projects"]![0]!["id"]!);
        Assert.Equal("ws1", (string)listing["workspaces"]![0]!["id"]!);
        Assert.All(build.Calls, c => Assert.Equal(adminId, c.PersonId));
    }
}
