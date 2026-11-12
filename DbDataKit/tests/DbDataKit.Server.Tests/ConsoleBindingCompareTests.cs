using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Api;
using DbDataKit.Runner.Connections;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a IX25 (owner, 2026-11-12): the console compares its bindings and the connections it keeps with a Runner's and a Gateway's, marks what differs, and per item adopts the other side's value or publishes its own through plan 30's shared
/// route; one combined, read-only list of the posture choices of the console's table and the Runners'. A real console host with the embedded runner on, a Gateway and a second runner that are pairable fakes of the shared publish API (behind the
/// token check and the roles of a real server, <see cref="FakeGroup"/>).
/// </summary>
public sealed class ConsoleBindingCompareTests : IAsyncLifetime
{
    private const string Claude = "schema: connection/2\nkind: model-provider\nname: claude\nsign_in: { method: api_key, key_secret: secret:dbdatakit:server:connection:claude:key }\nmodel_provider:\n  provider: anthropic\n  models:\n    - { id: claude-sonnet-4-5, window: 200000, max_output: 8192, price: { input: 3, output: 15 } }\n";
    private const string Local = "schema: connection/2\nkind: model-provider\nname: local\nmodel_provider:\n  provider: ollama\n  models:\n    - { id: llama3.1:8b, window: 8000 }\n";
    private const string Bindings = "bindings: 1\nmodels:\n  default:\n    prod: [ { connection: claude, model: claude-sonnet-4-5 } ]\n    test: [ { connection: local, model: \"llama3.1:8b\" } ]\n    qa: [ { connection: local, model: \"llama3.1:8b\" } ]\n";
    private const string Crm = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://crm.example/api\nsign_in:\n  method: api_key\n  name: X-Key\n  key_secret: 'env:DBDATAKIT_TEST_IX25_KEY'\n";

    /// <summary>A server's shared publish API as far as the compare uses it, with the roles it asks for and every call with the person the token names.</summary>
    private sealed class FakeGroup
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentDictionary<string, string> Agent { get; } = new();                       // "channel|slot" to bound_to, of the agent 'helper'
        public string? Refuse { get; set; }
        public JsonArray Posture { get; } = [];

        public void Map(WebApplication server, PairingCodes codes, PairedClientsFile clients)
        {
            IResult Needs(HttpContext c, ServerRole role, Func<IResult> then)
            {
                Calls.Enqueue($"{c.Request.Method} {c.Request.Path.Value!["/api/v1/".Length..]} by {c.ManagementToken().SubjectName}");
                return ManagementAuthorization.Authorize(c.ManagementToken(), role).Allowed ? then() : Results.Json(new { error = $"This needs the {role.ToString().ToLowerInvariant()} role." }, statusCode: 403);
            }

            server.MapGet("/api/v1/definitions/{kind}", (HttpContext c, string kind) => Needs(c, ServerRole.Viewer, () =>
                kind == "agent" ? Results.Json(new { definitions = new[] { new { name = "helper" } } })
                : Results.Json(new { error = "There is no such kind." }, statusCode: 404)));
            server.MapGet("/api/v1/definitions/{kind}/{name}", (HttpContext c, string kind, string name) => Needs(c, ServerRole.Viewer, () =>
                kind == "agent"
                    ? Results.Json(new
                    {
                        name, latest_version = 1, channels = new[] { new { channel = "prod", active = (int?)1 } },
                        bindings = Agent.Select(a => new { channel = a.Key.Split('|')[0], slot = a.Key.Split('|')[1], bound_to = a.Value, revision = 1 }),
                    })
                    : Results.Json(new { error = "There is no such definition." }, statusCode: 404)));
            server.MapPut("/api/v1/bindings/{channel}/{kind}/{name}", async (HttpContext c, string channel, string kind, string name) =>
            {
                var body = JsonNode.Parse(await new StreamReader(c.Request.Body).ReadToEndAsync())!.AsObject();
                return Needs(c, ServerRole.Admin, () =>
                {
                    if (Refuse is { } words) return Results.Json(new { error = words }, statusCode: 409);
                    foreach (var (slot, value) in body["slots"]!.AsObject()) Agent[$"{channel}|{slot}"] = (string)value!;
                    return Results.Json(new { name, channel });
                });
            });
            server.MapDelete("/api/v1/bindings/{channel}/{kind}/{name}/{slot}", (HttpContext c, string channel, string kind, string name, string slot) => Needs(c, ServerRole.Admin, () =>
                Agent.TryRemove($"{channel}|{slot}", out _) ? Results.Json(new { slot, unbound = true }) : Results.Json(new { error = "not bound" }, statusCode: 404)));
            server.MapGet("/api/v1/posture-choices", (HttpContext c) => Needs(c, ServerRole.Viewer, () => Results.Json(new { choices = Posture })));
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-ix25-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeGroup _gateway = new();
    private readonly FakeGroup _otherRunner = new();
    private PairableServer _gatewayServer = null!;
    private PairableServer _runnerServer = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _admin = "";
    private string _viewer = "";

    private sealed class NoRun : ITaskRunner
    {
        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) => throw new InvalidOperationException("nothing is scheduled in this test");
    }

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("DBDATAKIT_TEST_IX25_KEY", null);
        Directory.CreateDirectory(Path.Combine(_directory, "connections"));
        File.WriteAllText(Path.Combine(_directory, "connections", "claude.yml"), Claude);
        File.WriteAllText(Path.Combine(_directory, "connections", "local.yml"), Local);
        File.WriteAllText(Path.Combine(_directory, "connections", "crm.yml"), Crm);
        File.WriteAllText(Path.Combine(_directory, "bindings.yml"), Bindings);

        _gateway.Agent["prod|model:default"] = "claude/claude-sonnet-4-5";          // the same as the console's
        _gateway.Agent["test|model:default"] = "claude/claude-sonnet-4-5";          // the console says local for test
        _gateway.Agent["dev|model:default"] = "local/llama3.1:8b";                   // the console has no dev
        _gateway.Agent["prod|other-slot"] = "erp";                                   // a slot that stands for no console slot is not an item
        _gatewayServer = await PairableServer.StartAsync(product: ServerProducts.Gateway, instanceId: "gateway-1", map: _gateway.Map);
        _runnerServer = await PairableServer.StartAsync(product: ServerProducts.Runner, instanceId: "runner-2", map: _otherRunner.Map);

        var runner = new ConsoleRunnerSettings { Enabled = true, Tick = TimeSpan.FromMilliseconds(200), CancellationGrace = TimeSpan.FromSeconds(5), TaskHostPath = typeof(RunnerHost).Assembly.Location, WrapRunner = _ => new NoRun() };
        var settings = new ConsoleSettings(_directory, new Uri("https://localhost:0")) { Runner = runner };
        _app = ConsoleHost.Build(settings, ConsoleHost.Certificate(_directory), _ => { }, webRoot: _directory);
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(_app.Urls.First()) };
        _admin = Session(UserRole.Admin);
        _viewer = Session(UserRole.Viewer);
        var instances = _app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(_gatewayServer.Url, _gatewayServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Gateway", default);
        await instances.PairAsync(_runnerServer.Url, _runnerServer.NewCodeWith(ServerRole.Admin | ServerRole.Operator), "Other runner", default);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("DBDATAKIT_TEST_IX25_KEY", null);
        _http.Dispose();
        try { await _app.StopAsync(); } catch (ObjectDisposedException) { }
        await _app.DisposeAsync();
        await _gatewayServer.DisposeAsync();
        await _runnerServer.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string _adminId = "";

    private string Session(UserRole role)
    {
        var user = _app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role);
        if (role == UserRole.Admin) _adminId = user.Id;
        return _app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id;
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { }
        return (response.StatusCode, json);
    }

    private StateDatabase State => _app.Services.GetRequiredService<StateDatabase>();

    private async Task<JsonArray> Items(string server = "Gateway")
    {
        var (status, view) = await Call(HttpMethod.Get, $"/api/bindings/compare?server={server}", _viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        return view!["items"]!.AsArray();
    }

    private static JsonObject Item(JsonArray items, string id) => items.Single(i => (string)i!["id"]! == id)!.AsObject();

    private object Decision(JsonObject item, string server = "Gateway") =>
        new { server, family = (string)item["family"]!, name = (string)item["name"]!, definitionKind = (string?)item["definitionKind"], slot = (string?)item["slot"], channel = (string?)item["channel"], expected = (string?)item["there"] };

    [Fact]
    public async Task A_value_that_differs_is_listed_with_each_side_and_what_stands_for_no_console_slot_is_not()
    {
        var items = await Items();
        Assert.Equal("same", (string)Item(items, "binding/agent/helper/model:default/prod")["state"]!);
        var test = Item(items, "binding/agent/helper/model:default/test");
        Assert.Equal(("differs", "local/llama3.1:8b", "claude/claude-sonnet-4-5"), ((string)test["state"]!, (string)test["here"]!, (string)test["there"]!));
        Assert.True((bool)test["canAdopt"]! && (bool)test["canPublish"]!);
        var dev = Item(items, "binding/agent/helper/model:default/dev");
        Assert.Equal(("only-there", null), ((string)dev["state"]!, (string?)dev["here"]));
        var qa = Item(items, "binding/agent/helper/model:default/qa");
        Assert.Equal(("only-here", null), ((string)qa["state"]!, (string?)qa["there"]));
        Assert.DoesNotContain(items, i => (string?)i!["slot"] == "other-slot");

        // a Gateway shares no connection with the console (database connections there, outside systems here): bindings only
        Assert.DoesNotContain(items, i => (string)i!["family"]! == "connection");

        // read only: nothing was written on either side
        Assert.DoesNotContain(_gateway.Calls, c => !c.StartsWith("GET "));
        Assert.Equal(Bindings, File.ReadAllText(Path.Combine(_directory, "bindings.yml")));

        // a viewer reads; a server that is neither a runner nor a Gateway, or not there, is refused in words
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, "/api/bindings/compare?server=nowhere", _viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Get, "/api/bindings/compare", _viewer)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/bindings/compare?server=Gateway", null)).Status);
    }

    [Fact]
    public async Task Adopt_changes_the_consoles_file_through_its_own_reader_and_records_who_did_it_and_nothing_is_sent_to_the_server()
    {
        var test = Item(await Items(), "binding/agent/helper/model:default/test");
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _viewer, Decision(test))).Status);

        var (status, answer) = await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(test));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("same", (string)answer!["item"]!["state"]!);
        var file = File.ReadAllText(Path.Combine(_directory, "bindings.yml"));
        Assert.Contains("claude", file);
        var bindings = _app.Services.GetRequiredService<ConsoleBindings>();
        Assert.Equal("claude", bindings.Current.Find(DbDataKit.Connections.Bindings.SlotKind.ModelChat, "default")!.PerChannel["test"][0].Connection);
        Assert.Equal("local", bindings.Current.Find(DbDataKit.Connections.Bindings.SlotKind.ModelChat, "default")!.PerChannel["qa"][0].Connection);   // the rest of the slot is as it was
        Assert.Equal("same", (string)Item(await Items(), "binding/agent/helper/model:default/test")["state"]!);
        Assert.DoesNotContain(_gateway.Calls, c => !c.StartsWith("GET "));

        var audit = new AuditLog(State, ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "bindings.adopt"));
        var entry = Assert.Single(audit).Entry;
        Assert.Equal("person", entry.ActorKind);
        Assert.Equal(_adminId, entry.ActorId);
        Assert.Contains("claude/claude-sonnet-4-5", entry.Detail);
        Assert.Contains("Gateway", entry.Detail);

        // a slot the other side holds and the console does not is adopted whole; a value another side changed meanwhile is not
        var dev = Item(await Items(), "binding/agent/helper/model:default/dev");
        _gateway.Agent["dev|model:default"] = "claude/claude-sonnet-4-5";
        var stale = await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(dev));
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Contains("changed since you looked", (string)stale.Body!["error"]!);
        var fresh = Item(await Items(), "binding/agent/helper/model:default/dev");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(fresh))).Status);
        Assert.Equal("claude", bindings.Current.Find(DbDataKit.Connections.Bindings.SlotKind.ModelChat, "default")!.PerChannel["dev"][0].Connection);
    }

    [Fact]
    public async Task Adopt_refuses_a_value_the_console_cannot_keep_naming_why_and_changes_nothing()
    {
        _gateway.Agent["test|model:default"] = "ghost/model-x";                       // a connection the console does not have
        var test = Item(await Items(), "binding/agent/helper/model:default/test");
        var (status, answer) = await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(test));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("ghost", answer!["diagnostics"]!.ToJsonString());
        Assert.Equal(Bindings, File.ReadAllText(Path.Combine(_directory, "bindings.yml")));

        _gateway.Agent["test|model:default"] = "claude";                              // a model slot's choice names its model
        var bare = Item(await Items(), "binding/agent/helper/model:default/test");
        var (bad, words) = await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(bare));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad);
        Assert.Contains("names no model", (string)words!["error"]!);
    }

    [Fact]
    public async Task Publish_reaches_a_gateway_by_the_shared_route_as_the_signed_in_person_and_is_audited_and_an_only_there_binding_is_emptied()
    {
        var test = Item(await Items(), "binding/agent/helper/model:default/test");
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/bindings/compare/publish", _viewer, Decision(test))).Status);

        var (status, answer) = await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, Decision(test));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("same", (string)answer!["item"]!["state"]!);
        Assert.Equal("local/llama3.1:8b", _gateway.Agent["test|model:default"]);
        Assert.Contains("PUT bindings/test/agent/helper by Admin person", _gateway.Calls);               // plan 30's route, the person as the actor

        // the console's value for a channel the Gateway has none for
        var qa = Item(await Items(), "binding/agent/helper/model:default/qa");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, Decision(qa))).Status);
        Assert.Equal("local/llama3.1:8b", _gateway.Agent["qa|model:default"]);

        // the console holds none for dev: publishing that empties the Gateway's slot
        var dev = Item(await Items(), "binding/agent/helper/model:default/dev");
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, Decision(dev))).Status);
        Assert.False(_gateway.Agent.ContainsKey("dev|model:default"));
        Assert.Contains("DELETE bindings/dev/agent/helper/model:default by Admin person", _gateway.Calls);

        var audit = new AuditLog(State, ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "bindings.publish"));
        Assert.Equal(3, audit.Count);
        Assert.All(audit, a => Assert.Equal("Gateway", a.Entry.TargetId!.Split('/')[0]));

        // the Gateway's refusal comes back in its words and the console records nothing
        _gateway.Refuse = "The slot is locked.";
        _gateway.Agent["prod|model:default"] = "local/llama3.1:8b";
        var prod = Item(await Items(), "binding/agent/helper/model:default/prod");
        var (refused, words) = await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, Decision(prod));
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("The slot is locked.", (string)words!["error"]!);
        Assert.Equal(3, new AuditLog(State, ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "bindings.publish")).Count);
    }

    [Fact]
    public async Task A_connection_of_the_console_is_not_published_to_a_gateway_which_keeps_database_connections_and_says_so()
    {
        var (status, answer) = await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, new { server = "Gateway", family = "connection", name = "crm" });
        Assert.Equal(HttpStatusCode.NotFound, status);                       // nothing to compare: there is no such item on a Gateway
        Assert.Contains("nothing to compare", (string)answer!["error"]!);
        Assert.DoesNotContain(_gateway.Calls, c => !c.StartsWith("GET "));
    }

    [Fact]
    public async Task The_embedded_runners_connection_copy_is_compared_by_version_published_by_the_connection_copy_call_and_adopted_back()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/bindings/runner/console-runner/orders/source", _admin, new RunnerSlotBody(new() { ["prod"] = "crm" }))).Status);
        var items = await Items("console-runner");
        var same = Item(items, "connection/crm");
        Assert.Equal("same", (string)same["state"]!);
        Assert.Equal(ConnectionCopyVersion.Of(Crm), (string)same["there"]!);

        // the console's file changes: the runner's copy is older, publishing sends the connection again
        File.WriteAllText(Path.Combine(_directory, "connections", "crm.yml"), Crm + "# changed\n");
        var differs = Item(await Items("console-runner"), "connection/crm");
        Assert.Equal("differs", (string)differs["state"]!);
        var (published, _) = await Call(HttpMethod.Post, "/api/bindings/compare/publish", _admin, Decision(differs, "console-runner"));
        Assert.Equal(HttpStatusCode.OK, published);
        Assert.Equal("same", (string)Item(await Items("console-runner"), "connection/crm")["state"]!);
        Assert.Equal(ConnectionCopyVersion.Of(Crm + "# changed\n"), new ConnectionCopies(State).Get("crm")!.Version);

        // the runner's copy is changed on the runner; adopting writes it into the console's file
        new RunnerConnectionService(State, ConsoleState.ServiceId).Put("crm", Crm + "# on the runner\n", new RunnerActor("ops", "ops", "command", "console-runner"));
        var theirs = Item(await Items("console-runner"), "connection/crm");
        Assert.Equal("differs", (string)theirs["state"]!);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/bindings/compare/adopt", _admin, Decision(theirs, "console-runner"))).Status);
        Assert.Contains("# on the runner", File.ReadAllText(Path.Combine(_directory, "connections", "crm.yml")));
        Assert.Equal("same", (string)Item(await Items("console-runner"), "connection/crm")["state"]!);
    }

    [Fact]
    public async Task The_posture_choices_of_both_tables_are_one_read_only_list_and_a_production_binding_in_a_non_production_channel_appears_once_whichever_table_holds_it()
    {
        var consoleTable = new PostureChoices(State, BindingPostureModule.Name);
        var runnerTable = new PostureChoices(State);                                   // the embedded runner's
        const string Setting = "production_binding_on_non_production";
        consoleTable.Record("bindings/delivery/alerts/test", Setting, "ada", "the live hook", new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        runnerTable.Record("bindings/runner/orders/source/dev", Setting, "bo", "the live crm", new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero));
        // the same production binding recorded in both tables (it was moved, or chosen twice): one line, both holders named
        consoleTable.Record("bindings/runner/orders/source/dev", Setting, "cy", "again", new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));
        _otherRunner.Posture.Add(new JsonObject { ["setting"] = "run_in_process", ["scope"] = "*", ["chosen_by"] = "di", ["chosen_at"] = "2026-11-09T09:00:00+00:00", ["note"] = null });

        var (status, view) = await Call(HttpMethod.Get, "/api/bindings/posture", _viewer);
        Assert.Equal(HttpStatusCode.OK, status);
        var entries = view!["entries"]!.AsArray();
        Assert.Equal(3, entries.Count);
        var once = Assert.Single(entries, e => (string)e!["scope"]! == "bindings/runner/orders/source/dev")!;
        Assert.Equal(["console", "the console's runner"], once["heldBy"]!.AsArray().Select(h => (string)h!).ToArray());
        Assert.Equal("bo", (string)once["chosenBy"]!);                                  // the first record is the one shown
        Assert.Contains("that production uses", (string)once["effect"]!);
        Assert.Equal(["other runner"], entries.Where(e => (string)e!["setting"]! == "run_in_process").Select(e => ((string)e!["heldBy"]![0]!).ToLowerInvariant()).ToArray());
        Assert.Equal(["console", "the console's runner", "Other runner"], view["sources"]!.AsArray().Select(s => (string)s!["holder"]!).ToArray());
        Assert.All(view["sources"]!.AsArray(), s => Assert.True((bool)s!["ok"]!));

        // read only: the list is a GET, enforcement stays with the table that holds the choice
        Assert.Equal(1, consoleTable.List().Count(c => c.Scope == "bindings/runner/orders/source/dev"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(HttpMethod.Get, "/api/bindings/posture", null)).Status);

        // a runner that does not answer is named and the rest is listed
        await _runnerServer.DisposeAsync();
        var (_, partial) = await Call(HttpMethod.Get, "/api/bindings/posture", _viewer);
        Assert.Equal(3 - 1, partial!["entries"]!.AsArray().Count);
        Assert.Contains(partial["sources"]!.AsArray(), s => (string)s!["holder"]! == "Other runner" && !(bool)s["ok"]!);
    }
}
