using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataStore.Tests;

/// <summary>Plan 29b C05: the Store's management API tells a console what its state is and puts the server into maintenance, audited, with the shared <c>DbDataKit.State.Api</c>.</summary>
public sealed class StoreStateApiTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("dds-c05").FullName;
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private WebApplication? app;

    public void Dispose()
    {
        if (app is not null) { app.StopAsync().GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(HttpClient Http, Func<string> Viewer, Func<string> Admin, StoreSettings Settings)> Start(ScratchStateDatabase? db = null)
    {
        if (db is not null) new StateSettingsWriter().Write(Path.Combine(directory, "server.yaml"), new StateSettings(db.Engine, db.Connection));
        var resolved = StoreState.Resolve(directory, db is null ? null : db.Environment);
        var settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0")) { State = resolved.Settings, StateSources = resolved.Sources };
        app = StoreServer.Build(settings, StoreServer.Certificate(directory), _ => { });
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        var code = StoreState.Pairing(directory, db is null ? null : db.Environment).Codes().Create(ServerRoles.Close(ServerRole.Admin));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url };
        // a token is good for one call: each call asks for its own
        string Token(string user, ServerRole role) => console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, user, user, role));
        return (http, () => Token("vera", ServerRole.Viewer), () => Token("ada", ServerRole.Admin), settings);
    }

    private static async Task<(HttpResponseMessage Response, string Text)> Send(HttpClient http, HttpMethod method, string path, string? token, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        if (json is not null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Path.Combine(directory, "management-audit.log")).Read();

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_viewer_reads_the_state_with_a_masked_connection_and_the_module_versions(string engine)
    {
        using var scratch = engine == "sqlite" ? null : ScratchStateDatabase.For(engine);
        var (http, viewer, _, _) = await Start(scratch);
        using var _ = http;

        var (response, text) = await Send(http, HttpMethod.Get, "/api/v1/state", viewer());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = JsonNode.Parse(text)!;
        Assert.Equal("dbdatastore", (string?)state["product"]);
        Assert.Equal(StoreServer.Version, (string?)state["software_version"]);
        Assert.Equal(engine == "sqlite" ? StateEngineIds.Sqlite : engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, (string?)state["engine"]);
        Assert.True((bool)state["connected"]!);
        Assert.Equal(0, (int)state["pending_migrations"]!);
        Assert.Contains(state["modules"]!.AsArray(), m => (string?)m!["module"] == "trust" && (int)m["binary_version"]! == (int)m["registered_version"]!);
        Assert.False((bool)state["maintenance"]!["enabled"]!);
        if (scratch is not null)
        {
            Assert.Contains("env:" + scratch.PasswordVariable, (string?)state["connection"]);   // a reference as written, never what it resolves to
            Assert.Equal("file", (string?)state["sources"]!["engine"]!["kind"]);
            Assert.DoesNotContain(scratch.Environment(scratch.PasswordVariable)!, text);
        }
        else Assert.Equal("default", (string?)state["sources"]!["engine"]!["kind"]);
    }

    [Fact]
    public async Task A_viewer_cannot_enable_maintenance_and_the_refusal_is_audited()
    {
        var (http, viewer, _, _) = await Start();
        using var _ = http;

        var (response, _) = await Send(http, HttpMethod.Post, "/api/v1/state/maintenance", viewer(), """{"enabled": true, "reason": "no"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/v1/info", viewer())).Response.StatusCode);
        var entry = Assert.Single(Audit());
        Assert.Equal(("state.maintenance", "refused", "vera"), (entry.Command, entry.Outcome, entry.UserId));
    }

    [Fact]
    public async Task An_admin_puts_the_server_into_maintenance_and_back_and_each_step_is_audited_with_the_actor()
    {
        var (http, viewer, admin, _) = await Start();
        using var _ = http;

        var (enabled, enabledText) = await Send(http, HttpMethod.Post, "/api/v1/state/maintenance", admin(), """{"enabled": true, "reason": "moving the state"}""");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var status = JsonNode.Parse(enabledText)!;
        Assert.True((bool)status["enabled"]!);
        Assert.True((bool)status["idle"]!);

        // an ordinary call is refused with the reason and Retry-After; the state, the health endpoint and the administrator still work
        var (refused, refusedText) = await Send(http, HttpMethod.Get, "/api/v1/info", viewer());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal("moving the state", (string?)JsonNode.Parse(refusedText)!["reason"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/v1/state", viewer())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/v1/state/maintenance", viewer())).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/health/live", null)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/v1/info", admin())).Response.StatusCode);

        var (disabled, _) = await Send(http, HttpMethod.Post, "/api/v1/state/maintenance", admin(), """{"enabled": false}""");
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/v1/info", viewer())).Response.StatusCode);

        var entries = Audit();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => { Assert.Equal("state.maintenance", e.Command); Assert.Equal("ok", e.Outcome); Assert.Equal("ada", e.UserId); Assert.Equal("console-1", e.ConsoleId); });
        Assert.Contains("moving the state", entries[0].Target);
        Assert.Equal("disable", entries[1].Target);
    }
}
