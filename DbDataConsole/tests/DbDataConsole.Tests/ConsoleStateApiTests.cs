using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>Plan 29b C05: the console's own state, for the people signed in to it: <c>GET /api/v1/state</c> for a viewer, the maintenance mode for an administrator, audited in the state.</summary>
public sealed class ConsoleStateApiTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("ddk-console-c05").FullName;
    private WebApplication? app;
    private HttpClient? http;
    private ScratchStateDatabase? scratch;

    public void Dispose()
    {
        http?.Dispose();
        if (app is not null) { app.StopAsync().GetAwaiter().GetResult(); app.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        scratch?.Dispose();
        Npgsql.NpgsqlConnection.ClearAllPools();
        Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task Start(string engine)
    {
        StateSettings? state = null;
        StateSources? sources = null;
        if (engine != "sqlite")
        {
            scratch = ScratchStateDatabase.For(engine);
            state = new StateSettings(scratch.Engine, scratch.Connection);
            sources = new StateSources(new StateSource(StateSourceKind.Environment, "TEST_ENGINE"), StateSource.Default, StateSource.Default, StateSource.Default);
        }

        var settings = new ConsoleSettings(directory, new Uri("https://localhost:0"), State: state) { StateSources = sources };
        app = ConsoleHost.Build(settings, ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
    }

    private string SessionOf(string name, UserRole role) =>
        app!.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpResponseMessage Response, string Text)> Send(HttpMethod method, string path, string? session, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http!.SendAsync(request);
        return (response, await response.Content.ReadAsStringAsync());
    }

    private IReadOnlyList<AuditRecord> Audited() => new AuditLog(app!.Services.GetRequiredService<StateDatabase>(), ConsoleState.ServiceId).Read(new AuditQuery(Verb: "state.maintenance"));

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_viewer_reads_the_state_with_a_masked_connection_and_the_module_versions(string engine)
    {
        await Start(engine);
        var viewer = SessionOf("vera", UserRole.Viewer);

        var (response, text) = await Send(HttpMethod.Get, "/api/v1/state", viewer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = JsonNode.Parse(text)!;
        Assert.Equal("dbdatakit", (string?)state["product"]);
        Assert.Equal(engine == "sqlite" ? StateEngineIds.Sqlite : engine == "mssql" ? StateEngineIds.MsSql : StateEngineIds.Postgres, (string?)state["engine"]);
        Assert.True((bool)state["connected"]!);
        Assert.Equal(0, (int)state["pending_migrations"]!);
        var modules = state["modules"]!.AsArray();
        Assert.Contains(modules, m => (string?)m!["module"] == "identity" && (int)m["binary_version"]! == (int)m["registered_version"]!);
        Assert.Contains(modules, m => (string?)m!["module"] == "audit");
        Assert.False((bool)state["maintenance"]!["enabled"]!);
        if (scratch is not null)
        {
            Assert.Contains("env:" + scratch.PasswordVariable, (string?)state["connection"]);
            Assert.Equal("environment", (string?)state["sources"]!["engine"]!["kind"]);
            Assert.DoesNotContain(scratch.Environment(scratch.PasswordVariable)!, text);
        }

        // nobody signed in reads nothing
        Assert.NotEqual(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/state", null)).Response.StatusCode);
    }

    [Fact]
    public async Task A_viewer_cannot_enable_maintenance_and_the_refusal_is_in_the_audit()
    {
        await Start("sqlite");
        var viewer = SessionOf("vera", UserRole.Viewer);

        var (response, _) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", viewer, """{"enabled": true, "reason": "no"}""");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var entry = Assert.Single(Audited());
        Assert.Equal("refused", entry.Entry.Outcome);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/activity/runs", viewer)).Response.StatusCode);
    }

    [Fact]
    public async Task An_admin_puts_the_console_into_maintenance_and_ordinary_requests_get_503_while_the_state_and_the_admin_still_work()
    {
        await Start("sqlite");
        var viewer = SessionOf("vera", UserRole.Viewer);
        var admin = SessionOf("ada", UserRole.Admin);

        var (enabled, enabledText) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", admin, """{"enabled": true, "reason": "moving the state to PostgreSQL"}""");
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((bool)JsonNode.Parse(enabledText)!["enabled"]!);
        Assert.True((bool)JsonNode.Parse(enabledText)!["idle"]!);

        var (refused, refusedText) = await Send(HttpMethod.Get, "/api/activity/runs", viewer);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.Equal("moving the state to PostgreSQL", (string?)JsonNode.Parse(refusedText)!["reason"]);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/state", viewer)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/v1/state/maintenance", viewer)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/activity/runs", admin)).Response.StatusCode);   // the administrator goes on
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/health/live", null)).Response.StatusCode);

        var (left, _) = await Send(HttpMethod.Post, "/api/v1/state/maintenance", admin, """{"enabled": false}""");
        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/api/activity/runs", viewer)).Response.StatusCode);

        var audit = Audited();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, r => Assert.Equal("ok", r.Entry.Outcome));
        Assert.Contains(audit, r => r.Entry.TargetId!.Contains("moving the state to PostgreSQL"));
        Assert.Contains(audit, r => r.Entry.TargetId == "disable");
        Assert.All(audit, r => Assert.Equal("person", r.Entry.ActorKind));
    }
}
