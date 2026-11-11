using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 29b C06: state databases as connections of kind <c>state</c> (add, list with who uses them and the module versions found there, test, edit, remove) and the preflight and start of a move of a paired server onto one, against a stand-in
/// server that answers <c>/api/v1/state</c> and <c>/api/v1/state/move</c> as the shared component does. The real move is tested where it runs (State.Api.Tests) and by the Playwright story.
/// </summary>
public sealed class ConsoleStateConnectionsTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-c06-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConcurrentQueue<JsonNode> moveCalls = new();
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer store = null!;
    private ScratchStateDatabase? scratch;
    private ScratchStateDatabase? own;

    // what the stand-in server says about itself and its move call
    private string serverEngine = "Sqlite";
    private string? serverConnection;
    private int dryRunStatus = 200;
    private string dryRunReason = "";
    private bool offersMove = true;

    public async ValueTask InitializeAsync()
    {
        store = await PairableServer.StartAsync(product: "dbdatastore", instanceId: "store-1", map: (server, _, _) =>
        {
            server.MapGet("/api/v1/state", () => Results.Json(new JsonObject
            {
                ["product"] = "dbdatastore", ["engine"] = serverEngine, ["connection"] = serverConnection, ["file"] = serverEngine == "Sqlite" ? "/var/store/state.db" : null,
                ["maintenance"] = new JsonObject { ["enabled"] = false, ["idle"] = false },
            }));
            server.MapPost("/api/v1/state/move", async (HttpContext context) =>
            {
                if (!offersMove) return Results.Json(new { error = "There is no such call." }, statusCode: 404);
                var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
                moveCalls.Enqueue(body);
                if (body["dry_run"]?.GetValue<bool>() == true)
                    return dryRunStatus == 200 ? Results.Json(new { state = "preflight_ok", message = "The target opens and is empty." }) : Results.Json(new { error = "no secret", reason = dryRunReason }, statusCode: dryRunStatus);
                return Results.Json(new { state = "running", step = "preflight" }, statusCode: 202);
            });
            server.MapGet("/api/v1/state/move", () => Results.Json(new { state = "succeeded", step = "done", restart = "Restart the service now." }));
        });
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(store.Url, store.NewCodeWith(ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver), "Store one", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await store.DisposeAsync();
        scratch?.Dispose();
        own?.Dispose();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string Session(UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("Ana" + Guid.NewGuid().ToString("N")[..4], null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body, string Text)> Call(HttpMethod method, string path, object? body = null, UserRole role = UserRole.Admin)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={Session(role)}");
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null, text);
    }

    /// <summary>The scratch database as a connection string without its password, and the password: what a person types in the form.</summary>
    private (string Connection, string Password) Parts(ScratchStateDatabase db)
    {
        var password = db.Environment(db.PasswordVariable)!;
        var connection = db.Connection.Replace("env:" + db.PasswordVariable, "x");
        // remove the Password key whatever the provider calls it
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection };
        builder.Remove("Password");
        return (builder.ConnectionString, password);
    }

    private async Task Add(string name, ScratchStateDatabase db)
    {
        var (connection, password) = Parts(db);
        var (status, _, text) = await Call(HttpMethod.Put, $"/api/state-connections/{name}", new { engine = db.Engine, connection, password, note = "for the move" });
        Assert.True(status == HttpStatusCode.OK, text);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_state_connection_is_added_listed_tested_edited_and_removed_with_the_password_in_the_secret_store(string engine)
    {
        scratch = ScratchStateDatabase.For(engine);
        await Add("new-state", scratch);
        var password = scratch.Environment(scratch.PasswordVariable)!;

        // the file has the reference, the secret store has the password, and no response shows it
        var file = File.ReadAllText(Path.Combine(directory, "state-connections.json"));
        Assert.DoesNotContain(password, file);
        Assert.Contains("secret:dbdata:console:state:new-state", file);
        var (_, list, listText) = await Call(HttpMethod.Get, "/api/state-connections");
        Assert.DoesNotContain(password, listText);
        var row = Assert.Single(list!.AsArray())!;
        Assert.Equal("new-state", (string?)row["name"]);
        Assert.Contains("secret:dbdata:console:state:new-state", (string?)row["connection"]);
        Assert.True((bool)row["reachable"]!);
        Assert.True((bool)row["empty"]!);
        Assert.Equal("dbdata:console:state:new-state", (string?)row["secret"]);

        // test: the shared checks, one sentence
        var (testStatus, test, testText) = await Call(HttpMethod.Post, "/api/state-connections/new-state/test");
        Assert.Equal(HttpStatusCode.OK, testStatus);
        Assert.True((bool)test!["ok"]!);
        Assert.Contains("empty", (string?)test["sentence"]);
        Assert.Contains(test["steps"]!.AsArray(), s => (string?)s!["name"] == "connection" && (string?)s["status"] != "failed");
        Assert.Contains(test["steps"]!.AsArray(), s => (string?)s!["name"] == "engine");
        Assert.DoesNotContain(password, testText);

        // an edit with no password keeps the reference (and the stored secret): the test still passes
        var (connection, _) = Parts(scratch);
        var (editStatus, _, _) = await Call(HttpMethod.Put, "/api/state-connections/new-state", new { engine = scratch.Engine, connection, note = "edited" });
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Contains("secret:dbdata:console:state:new-state", File.ReadAllText(Path.Combine(directory, "state-connections.json")));
        Assert.True((bool)(await Call(HttpMethod.Post, "/api/state-connections/new-state/test")).Body!["ok"]!);

        // a viewer may not use any of it
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/state-connections", role: UserRole.Viewer)).Status);

        var (removed, _, _) = await Call(HttpMethod.Delete, "/api/state-connections/new-state");
        Assert.Equal(HttpStatusCode.NoContent, removed);
        Assert.Empty((await Call(HttpMethod.Get, "/api/state-connections")).Body!.AsArray());
    }

    [Fact]
    public async Task A_wrong_password_is_one_sentence_naming_the_fix_and_a_password_in_the_connection_string_is_refused()
    {
        scratch = ScratchStateDatabase.For("postgres");
        var (connection, _) = Parts(scratch);
        var (_, _, _) = await Call(HttpMethod.Put, "/api/state-connections/wrong", new { engine = "postgres", connection, password = "not-the-password" });
        var (_, test, text) = await Call(HttpMethod.Post, "/api/state-connections/wrong/test");
        Assert.False((bool)test!["ok"]!);
        Assert.Contains("refused", (string?)test["sentence"]);
        Assert.Contains("password", (string?)test["sentence"]);
        Assert.DoesNotContain("not-the-password", text);

        var (status, body, _) = await Call(HttpMethod.Put, "/api/state-connections/literal", new { engine = "postgres", connection = connection + ";Password=typed-here" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("password field", (string?)body!["error"]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/state-connections/Bad_Name", new { engine = "postgres", connection })).Status);
    }

    [Fact]
    public async Task A_sqlite_state_connection_is_a_path_on_the_console_machine()
    {
        var file = Path.Combine(directory, "other-state.db");
        var (status, _, _) = await Call(HttpMethod.Put, "/api/state-connections/lite", new { engine = "sqlite", path = file });
        Assert.Equal(HttpStatusCode.OK, status);
        var (_, test, _) = await Call(HttpMethod.Post, "/api/state-connections/lite/test");
        Assert.True((bool)test!["ok"]!);
        Assert.True((bool)test["empty"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/state-connections/relative", new { engine = "sqlite", path = "state.db" })).Status);
    }

    [Fact]
    public async Task The_list_says_which_servers_use_a_state_database_and_a_used_one_cannot_be_removed()
    {
        scratch = ScratchStateDatabase.For("postgres");
        await Add("shared", scratch);
        serverEngine = "Postgres";
        serverConnection = scratch.Connection;   // as the server reports it: references as written
        var (_, list, _) = await Call(HttpMethod.Get, "/api/state-connections");
        var users = list![0]!["usedBy"]!.AsArray();
        Assert.Equal("Store one", (string?)Assert.Single(users)!["name"]);

        var (status, body, _) = await Call(HttpMethod.Delete, "/api/state-connections/shared");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Store one", (string?)body!["error"]);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task The_move_preflight_checks_the_server_the_target_and_the_servers_own_view(string engine)
    {
        scratch = ScratchStateDatabase.For(engine);
        await Add("target", scratch);
        var (status, body, text) = await Call(HttpMethod.Post, "/api/state-moves/preflight", new { server = "Store one", connection = "target" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True((bool)body!["ok"]!, text);
        var checks = body["checks"]!.AsArray();
        Assert.Contains(checks, c => (string?)c!["name"] == "server" && (bool)c["ok"]!);
        Assert.Contains(checks, c => (string?)c!["name"] == "target empty" && (bool)c["ok"]!);
        Assert.Contains(checks, c => (string?)c!["name"] == "not the same database" && (bool)c["ok"]!);
        Assert.Contains(checks, c => (string?)c!["name"] == "server can open the target" && (bool)c["ok"]!);
        Assert.DoesNotContain(scratch.Environment(scratch.PasswordVariable)!, text);

        // the server cannot resolve the secret: the sentence names the command to run on that server
        dryRunStatus = 400;
        dryRunReason = "secretmissing";
        var missing = (await Call(HttpMethod.Post, "/api/state-moves/preflight", new { server = "Store one", connection = "target" })).Body!;
        Assert.False((bool)missing["ok"]!);
        Assert.Contains(missing["checks"]!.AsArray(), c => (string?)c!["name"] == "server has the secret" && ((string?)c["sentence"])!.Contains("dbdatastore secret set dbdata:console:state:target"));

        // a server too old for the call: the command line way
        offersMove = false;
        var old = (await Call(HttpMethod.Post, "/api/state-moves/preflight", new { server = "Store one", connection = "target" })).Body!;
        Assert.False((bool)old["ok"]!);
        Assert.Contains(old["checks"]!.AsArray(), c => ((string?)c!["sentence"])!.Contains("dbdatastore state move --to-engine"));
    }

    [Fact]
    public async Task The_preflight_refuses_the_servers_own_database_and_a_target_that_is_not_empty()
    {
        scratch = ScratchStateDatabase.For("postgres");
        await Add("target", scratch);
        serverEngine = "Postgres";
        serverConnection = scratch.Connection;
        var same = (await Call(HttpMethod.Post, "/api/state-moves/preflight", new { server = "Store one", connection = "target" })).Body!;
        Assert.False((bool)same["ok"]!);
        Assert.Contains(same["checks"]!.AsArray(), c => (string?)c!["name"] == "not the same database" && !(bool)c["ok"]!);

        // a database that already holds the console's modules is not empty
        own = ScratchStateDatabase.For("postgres");
        _ = ConsoleState.Open(Path.Combine(directory, "other"), new StateSettings(own.Engine, own.Connection));
        await Add("used", own);
        serverEngine = "Sqlite";
        var used = (await Call(HttpMethod.Post, "/api/state-moves/preflight", new { server = "Store one", connection = "used" })).Body!;
        Assert.False((bool)used["ok"]!);
        Assert.Contains(used["checks"]!.AsArray(), c => (string?)c!["name"] == "target empty" && !(bool)c["ok"]! && ((string?)c["sentence"])!.Contains("empty database"));
    }

    [Fact]
    public async Task Starting_the_move_sends_the_engine_and_the_secret_reference_never_the_password()
    {
        scratch = ScratchStateDatabase.For("postgres");
        await Add("target", scratch);
        var (status, body, text) = await Call(HttpMethod.Post, "/api/state-moves/start", new { server = "Store one", connection = "target" });
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("running", (string?)body!["state"]);
        var sent = moveCalls.Last();
        Assert.Equal("postgres", (string?)sent["engine"]);
        Assert.False(sent["dry_run"]!.GetValue<bool>());
        Assert.Contains("Password=secret:dbdata:console:state:target", (string?)sent["connection"]);
        Assert.DoesNotContain(scratch.Environment(scratch.PasswordVariable)!, sent.ToJsonString() + text);

        var (_, progress, _) = await Call(HttpMethod.Get, "/api/state-moves/" + Uri.EscapeDataString("store-1"));
        Assert.Equal("succeeded", (string?)progress!["state"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/state-moves/start", new { server = "Store one", connection = "target" }, UserRole.Viewer)).Status);
    }
}
