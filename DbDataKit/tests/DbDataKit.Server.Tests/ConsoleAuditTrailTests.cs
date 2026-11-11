using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>UI05: what the console records (sign-ins and invitations, servers, its own changes, the writes relayed to a server), the route that lists it and who may read it. The console's own SQLite state: no engine needed.</summary>
public sealed class ConsoleAuditTrailTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-audit-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync();
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private (string Session, string UserId) Session(UserRole role, string name)
    {
        var user = app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role);
        return (app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id, user.Id);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? session, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(request);
    }

    private async Task<JsonArray> Audit(string session, string query = "")
    {
        var response = await Send(HttpMethod.Get, "/api/audit" + query, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["entries"]!.AsArray();
    }

    [Theory]
    [InlineData("GET", "/relay/s1/api/things", null)]
    [InlineData("POST", "/relay/s1/api/models/m/check", null)]
    [InlineData("POST", "/relay/s1/api/models/m/diagnostics", null)]
    [InlineData("PUT", "/relay/s1/api/models/m", "relay.write")]
    [InlineData("DELETE", "/relay/s1/api/models/m/", "relay.write")]
    [InlineData("POST", "/api/instances", "server.pair")]
    [InlineData("POST", "/api/instances/pending/p1/confirm", "server.confirm")]
    [InlineData("DELETE", "/api/instances/pending/p1", "server.reject")]
    [InlineData("DELETE", "/api/instances/s1", "server.forget")]
    [InlineData("POST", "/api/invites", "invite.create")]
    [InlineData("POST", "/api/invites/complete-registration", "invite.redeem")]
    [InlineData("POST", "/api/invites/begin-registration", null)]
    [InlineData("POST", "/api/auth/passkey/begin", null)]
    [InlineData("POST", "/api/auth/passkey/complete", "signin.passkey")]
    [InlineData("POST", "/api/auth/sign-out", null)]
    [InlineData("PUT", "/api/users/u1", "user.update")]
    [InlineData("DELETE", "/api/users/u1/credentials/c1", "user.credential_remove")]
    [InlineData("POST", "/api/state-connections", "console.write")]
    [InlineData("PUT", "/api/state-connections/new-db", "state.connection.save")]
    [InlineData("DELETE", "/api/state-connections/new-db", "state.connection.remove")]
    [InlineData("POST", "/api/state-connections/new-db/test", null)]
    [InlineData("POST", "/api/state-moves/preflight", "state.move.check")]
    [InlineData("POST", "/api/state-moves/start", "state.move.start")]
    [InlineData("GET", "/api/state-moves/s1", null)]
    [InlineData("POST", "/api/v1/state/maintenance", null)]
    [InlineData("GET", "/api/audit", null)]
    [InlineData("PUT", "/health", null)]
    public void A_call_is_recorded_as_the_verb_the_rules_give_or_not_at_all(string method, string path, string? verb) =>
        Assert.Equal(verb, ConsoleAuditTrail.Describe(method, path)?.Verb);

    [Theory]
    [InlineData(200, "ok")]
    [InlineData(204, "ok")]
    [InlineData(401, "refused")]
    [InlineData(403, "refused")]
    [InlineData(404, "failed")]
    [InlineData(502, "failed")]
    public void The_outcome_is_ok_refused_or_failed(int status, string outcome) => Assert.Equal(outcome, ConsoleAuditTrail.OutcomeOf(status));

    [Fact]
    public async Task A_relayed_write_is_recorded_with_who_which_server_the_method_and_the_path_and_never_the_body()
    {
        var (admin, adminId) = Session(UserRole.Admin, "Ada");
        var paired = await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(DbDataKit.Web.Trust.ServerRole.Admin | DbDataKit.Web.Trust.ServerRole.Operator | DbDataKit.Web.Trust.ServerRole.Author | DbDataKit.Web.Trust.ServerRole.Approver), "Fake", default);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/relay/{paired.InstanceId}/api/echo", admin)).StatusCode);
        await Send(HttpMethod.Post, $"/relay/{paired.InstanceId}/api/echo/check", admin, "{}");   // asks a question: not a write
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/relay/{paired.InstanceId}/api/echo?x=1", admin, """{"password":"s3cret-value"}""")).StatusCode);

        var entries = await Audit(admin, "?verb=relay.");
        var entry = Assert.Single(entries)!;
        Assert.Equal("relay.write", (string)entry["verb"]!);
        Assert.Equal("ok", (string)entry["outcome"]!);
        Assert.Equal(adminId, (string)entry["actor"]!);
        Assert.Equal("Ada", (string)entry["actorName"]!);
        Assert.Equal(("server", paired.InstanceId), ((string)entry["targetKind"]!, (string)entry["targetId"]!));
        Assert.Equal("PUT", (string)entry["detail"]!["method"]!);
        Assert.Equal($"/relay/{paired.InstanceId}/api/echo", (string)entry["detail"]!["path"]!);
        Assert.Equal("Fake", (string)entry["detail"]!["serverName"]!);
        Assert.Equal(200, (int)entry["detail"]!["status"]!);
        Assert.DoesNotContain("s3cret-value", entry.ToJsonString());
        Assert.DoesNotContain("x=1", entry.ToJsonString());
    }

    [Fact]
    public async Task A_refused_change_is_recorded_as_refused_and_a_server_that_was_forgotten_keeps_its_name()
    {
        var (admin, adminId) = Session(UserRole.Admin, "Ada");
        var (viewer, viewerId) = Session(UserRole.Viewer, "Vic");
        var paired = await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCode(), "Fake", default);

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Delete, $"/api/instances/{paired.InstanceId}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/api/instances/{paired.InstanceId}", admin)).StatusCode);

        var entries = await Audit(admin, "?verb=server.");
        Assert.Equal(["server.forget", "server.forget"], entries.Select(e => (string)e!["verb"]!));
        Assert.Equal([("ok", adminId), ("refused", viewerId)], entries.Select(e => ((string)e!["outcome"]!, (string)e["actor"]!)));
        Assert.All(entries, e => Assert.Equal(paired.InstanceId, (string)e!["targetId"]!));
        Assert.Equal("Fake", (string)entries[0]!["detail"]!["serverName"]!);

        var refused = await Audit(admin, "?outcome=refused");
        Assert.Equal(viewerId, (string)Assert.Single(refused)!["actor"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/api/audit?outcome=maybe", admin)).StatusCode);
    }

    [Fact]
    public async Task A_new_server_is_recorded_under_its_own_id_and_an_invitation_without_a_person_is_not_a_line()
    {
        var (admin, _) = Session(UserRole.Admin, "Ada");
        var paired = await Send(HttpMethod.Post, "/api/instances", admin, $$"""{"url":"{{server.Url.AbsoluteUri}}","code":"{{server.NewCode()}}","name":"Paired here"}""");
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var id = (string)JsonNode.Parse(await paired.Content.ReadAsStringAsync())!["id"]!;
        // nobody is signed in: the policy's answer, not an act
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Post, "/api/instances", null, "{}")).StatusCode);

        var entry = Assert.Single(await Audit(admin, "?verb=server.pair"))!;
        Assert.Equal(("server", id), ((string)entry["targetKind"]!, (string)entry["targetId"]!));
        Assert.DoesNotContain("anonymous", (await Audit(admin)).ToJsonString());
    }

    [Fact]
    public async Task The_trail_is_read_by_administrators_only_pages_and_filters_by_person_and_time()
    {
        var (admin, adminId) = Session(UserRole.Admin, "Ada");
        var (viewer, _) = Session(UserRole.Viewer, "Vic");
        var log = app.Services.GetRequiredService<AuditLog>();
        for (var i = 0; i < 5; i++) log.Record(new AuditEvent("person", i % 2 == 0 ? adminId : "someone-else", "console.write", TargetKind: "path", TargetId: $"/api/thing/{i}"));
        // another service's entry is not the console's
        new AuditLog(app.Services.GetRequiredService<StateDatabase>(), "dbdatasync").Record(new AuditEvent("person", adminId, "sync.run.start"));

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/api/audit", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, "/api/audit", null)).StatusCode);

        var first = JsonNode.Parse(await (await Send(HttpMethod.Get, "/api/audit?limit=2", admin)).Content.ReadAsStringAsync())!;
        Assert.Equal(["/api/thing/4", "/api/thing/3"], first["entries"]!.AsArray().Select(e => (string)e!["targetId"]!));
        var next = (long)first["next"]!;
        var rest = await Audit(admin, $"?beforeId={next}&limit=100");
        Assert.Equal(["/api/thing/2", "/api/thing/1", "/api/thing/0"], rest.Select(e => (string)e!["targetId"]!));
        Assert.DoesNotContain("sync.run.start", (await Audit(admin)).ToJsonString());

        Assert.Equal(3, (await Audit(admin, $"?actor={adminId}")).Count);
        Assert.Empty(await Audit(admin, $"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"))}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, "/api/audit?limit=0", admin)).StatusCode);
    }
}
