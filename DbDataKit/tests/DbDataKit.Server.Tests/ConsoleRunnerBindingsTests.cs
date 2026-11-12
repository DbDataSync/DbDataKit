using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a IX23, owner decision of 2026-11-11: the Runner is a separate server and does not read the console's connections folder; the console hands it a connection DEFINITION with secret REFERENCES (never a value) when a runner slot is bound or sent again, and
/// the runner keeps its own copy. A real console host with the embedded runner on, over SQLite (the runner's own routes are tested with the real runner host in <c>DbDataRunner.Tests</c>).
/// </summary>
public sealed class ConsoleRunnerBindingsTests : IAsyncLifetime
{
    private const string Variable = "DBDATAKIT_TEST_IX23_CONSOLE_KEY";
    private const string KeyValue = "console-key-value-77d41e";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-runner-bindings-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _admin = "";
    private string _viewer = "";

    private static string Crm(string comment = "") =>
        $"schema: connection/2\nname: crm\nkind: http\nendpoint: https://crm.example/api\nsign_in:\n  method: api_key\n  name: X-Key\n  key_secret: 'env:{Variable}'\n{comment}";

    private sealed class NoRun : ITaskRunner
    {
        public Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default) => throw new InvalidOperationException("nothing is scheduled in this test");
    }

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        Directory.CreateDirectory(Path.Combine(_directory, "connections"));
        File.WriteAllText(Path.Combine(_directory, "connections", "crm.yml"), Crm());
        await StartAsync();
    }

    private async Task StartAsync()
    {
        var runner = new ConsoleRunnerSettings { Enabled = true, Tick = TimeSpan.FromMilliseconds(200), CancellationGrace = TimeSpan.FromSeconds(5), TaskHostPath = typeof(RunnerHost).Assembly.Location, WrapRunner = _ => new NoRun() };
        var settings = new ConsoleSettings(_directory, new Uri("https://localhost:0")) { Runner = runner };
        _app = ConsoleHost.Build(settings, ConsoleHost.Certificate(_directory), _ => { }, webRoot: _directory);
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(_app.Urls.First()) };
        _admin = Session(UserRole.Admin);
        _viewer = Session(UserRole.Viewer);
    }

    private string Session(UserRole role) =>
        _app.Services.GetRequiredService<ISessionStore>().Create(_app.Services.GetRequiredService<IUserAccounts>().CreateUser(role + " person", null, role).Id).Id;

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        try { await _app.StopAsync(); } catch (ObjectDisposedException) { }
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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

    private const string Slot = "/api/bindings/runner/console-runner/orders/source";
    private StateDatabase State => _app.Services.GetRequiredService<StateDatabase>();

    [Fact]
    public async Task Binding_a_runner_slot_sends_the_definition_with_its_references_the_runner_keeps_its_own_copy_and_the_screen_compares_versions_and_sends_again()
    {
        var version = ConnectionCopyVersion.Of(Crm());
        var (status, saved) = await Call(HttpMethod.Put, Slot, _admin, new RunnerSlotBody(new() { ["prod"] = "crm" }));
        Assert.Equal(HttpStatusCode.OK, status);
        var sent = Assert.Single(saved!["sent"]!.AsArray());
        Assert.Equal(version, (string)sent!["version"]!);
        var unresolved = Assert.Single(sent["unresolved"]!.AsArray());          // the reference does not resolve in the runner's store: the screen says so
        Assert.Equal($"env:{Variable}", (string)unresolved!["reference"]!);
        Assert.DoesNotContain(KeyValue, saved.ToJsonString());

        // the runner keeps its own copy in its own state, and says the same through its own route
        var copy = new ConnectionCopies(State).Get("crm")!;
        Assert.Equal(version, copy.Version);
        Assert.DoesNotContain(KeyValue, copy.Definition);
        var (_, listed) = await Call(HttpMethod.Get, "/relay/console-runner/api/v1/connection-copies", _viewer);
        var mine = Assert.Single(listed!["copies"]!.AsArray())!;
        Assert.Equal(1, (int)mine["unresolved"]!);
        Assert.Equal("orders", (string)mine["used_by"]![0]!["package"]!);

        // the binding is the runner's: slot to name, nothing of package/1 changed
        var bound = new DefinitionPublisher(State, RunnerPackages.Kinds(), new PayloadStore(State), "runner").Bindings("prod", RunnerPackages.Kind, "orders");
        Assert.Equal(("source", "crm"), (bound.Single().Slot, bound.Single().BoundTo));

        // the console's version is on the Bindings view; changing the file makes the copy older until it is sent again
        var (_, view) = await Call(HttpMethod.Get, "/api/bindings", _viewer);
        Assert.Equal(version, (string)view!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "crm")!["version"]!);
        File.WriteAllText(Path.Combine(_directory, "connections", "crm.yml"), Crm("# changed\n"));
        var newer = ConnectionCopyVersion.Of(Crm("# changed\n"));
        Assert.NotEqual(version, newer);
        Assert.Equal(newer, (string)(await Call(HttpMethod.Get, "/api/bindings", _viewer)).Body!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "crm")!["version"]!);
        Assert.Equal(version, (string)(await Call(HttpMethod.Get, "/relay/console-runner/api/v1/connection-copies", _viewer)).Body!["copies"]![0]!["version"]!);

        Environment.SetEnvironmentVariable(Variable, KeyValue);
        var (again, resent) = await Call(HttpMethod.Post, "/api/bindings/runner/console-runner/connections/crm/send", _admin);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(newer, (string)resent!["version"]!);
        Assert.Empty(resent["unresolved"]!.AsArray());                           // the runner resolves it now, on the runner
        Assert.Equal(newer, (string)(await Call(HttpMethod.Get, "/relay/console-runner/api/v1/connection-copies", _viewer)).Body!["copies"]![0]!["version"]!);
        Assert.DoesNotContain(KeyValue, resent.ToJsonString());

        // audited by the runner with the name and version only
        var audit = new AuditLog(State, ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "runner.connection.put"));
        Assert.Equal(2, audit.Count);
        Assert.DoesNotContain(audit, e => (e.Entry.Detail ?? "").Contains(KeyValue));
    }

    [Fact]
    public async Task Only_an_administrator_changes_a_runner_slot_or_sends_a_connection_and_a_connection_the_console_does_not_keep_is_bound_by_name_with_nothing_sent()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, Slot, _viewer, new RunnerSlotBody(new() { ["prod"] = "crm" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/bindings/runner/console-runner/connections/crm/send", _viewer)).Status);
        Assert.Empty(new ConnectionCopies(State).List());

        var (status, answer) = await Call(HttpMethod.Put, Slot, _admin, new RunnerSlotBody(new() { ["prod"] = "erp" }));   // a name the runner knows itself
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(answer!["sent"]!.AsArray());
        Assert.Empty(new ConnectionCopies(State).List());
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/bindings/runner/console-runner/connections/nothing/send", _admin)).Status);
    }

    [Fact]
    public async Task A_non_production_channel_on_the_connection_production_uses_is_refused_with_409_until_confirmed_and_the_runner_records_the_choice_in_its_own_table()
    {
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, Slot, _admin, new RunnerSlotBody(new() { ["prod"] = "crm" }))).Status);

        var both = new RunnerSlotBody(new() { ["prod"] = "crm", ["dev"] = "crm" });
        var (status, refused) = await Call(HttpMethod.Put, Slot, _admin, both);
        Assert.Equal(HttpStatusCode.Conflict, status);
        var need = Assert.Single(refused!["needsConfirmation"]!.AsArray())!;
        Assert.Equal("bindings/runner/orders/source/dev", (string)need["scope"]!);
        Assert.Equal(("runner", "orders/source", "dev", "crm"), ((string)need["section"]!, (string)need["slot"]!, (string)need["release"]!, (string)need["connection"]!));
        Assert.Contains("which production uses", (string)need["effect"]!);
        Assert.Empty(new PostureChoices(State).List());

        var (ok, _) = await Call(HttpMethod.Put, Slot, _admin, both with { Confirm = ["bindings/runner/orders/source/dev"], Note = "the dev crm is the live one this week" });
        Assert.Equal(HttpStatusCode.OK, ok);
        var choice = Assert.Single(new PostureChoices(State).List());
        Assert.Equal(("bindings/runner/orders/source/dev", PostureSetting.ProductionBindingOnNonProduction.Name, "the dev crm is the live one this week"), (choice.Scope, choice.Setting, choice.Note));
        Assert.Empty(new PostureChoices(State, BindingPostureModule.Name).List());       // the console's own table is for its own bindings

        // its own connection takes the choice back
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, Slot, _admin, new RunnerSlotBody(new() { ["dev"] = "crm-sandbox" }))).Status);
        Assert.Empty(new PostureChoices(State).List());
    }

    [Fact]
    public async Task The_choices_of_the_earlier_file_move_into_the_consoles_own_table_once_keeping_who_and_when_and_the_file_is_renamed_never_deleted()
    {
        await DisposeAsync();
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(Path.Combine(_directory, "connections"));
        File.WriteAllText(Path.Combine(_directory, ConsoleBindings.EarlierChoicesFileName),
            """{ "bindings/delivery/alerts/test": { "Scope": "bindings/delivery/alerts/test", "ChosenBy": "ada", "ChosenAt": "2026-11-10T09:30:00+00:00", "Note": "the live hook this week" } }""");
        await StartAsync();

        var bindings = _app.Services.GetRequiredService<ConsoleBindings>();
        Assert.True(bindings.Confirmed("bindings/delivery/alerts/test"));
        var choice = bindings.ChoiceOf("bindings/delivery/alerts/test")!;
        Assert.Equal(("ada", "the live hook this week"), (choice.ChosenBy, choice.Note));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 9, 30, 0, TimeSpan.Zero), choice.ChosenAt);
        Assert.False(File.Exists(Path.Combine(_directory, ConsoleBindings.EarlierChoicesFileName)));
        Assert.True(File.Exists(Path.Combine(_directory, ConsoleBindings.EarlierChoicesFileName + ".moved")));
        Assert.Single(new PostureChoices(State, BindingPostureModule.Name).List());
        Assert.Contains(new AuditLog(State, ConsoleState.ServiceId).Read(new AuditQuery(VerbPrefix: "bindings.posture.move")), e => e.Entry.Outcome == AuditEvent.Ok);
    }
}
