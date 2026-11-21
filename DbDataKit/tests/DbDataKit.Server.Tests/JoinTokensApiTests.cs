using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The routes of the Add a server screen: an administrator makes a token and gets the command once, lists the waiting tokens (never a value) and cancels
/// one, audited; a viewer can do none of it. The console's own SQLite state: no engine needed.
/// </summary>
public sealed class JoinTokensApiTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-join-tokens-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), PublicUrl: new Uri("https://console.example.com:7800")),
            ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private (string Session, string UserId) Session(UserRole role, string name)
    {
        var user = app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role);
        return (app.Services.GetRequiredService<ISessionStore>().Create(user.Id).Id, user.Id);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string session, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(request);
    }

    private async Task<JsonNode> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    private IReadOnlyList<AuditRecord> Audit(string verb) =>
        app.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: verb));

    [Fact]
    public async Task An_administrator_makes_a_token_and_sees_its_command_once_and_the_waiting_list_never_shows_a_value()
    {
        var (admin, adminId) = Session(UserRole.Admin, "Ada");
        using var made = await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"runner","roles":["viewer","operator"],"group":"night","minutes":30,"url":"https://console.example.com:7800/"}""");
        var answer = await Json(made, HttpStatusCode.OK);
        Assert.Equal("no-store", made.Headers.CacheControl?.ToString());
        var command = (string)answer["command"]!;
        var certificate = ConsoleHost.Certificate(directory);
        Assert.StartsWith("dbdatarunner pair --host https://console.example.com:7800 --token ", command);
        Assert.EndsWith($"--console-thumbprint {Thumbprints.Format(PairingProof.CertificateFingerprint(certificate))}", command);
        var token = command.Split(' ')[5];
        Assert.NotNull(JoinTokens.Normalize(token));
        Assert.Equal(("runner", "night", false), ((string)answer["product"]!, (string?)answer["group"], (bool)answer["loopback"]!));
        Assert.Equal(["operator", "viewer"], answer["roles"]!.AsArray().Select(r => (string)r!).Order());

        // the console keeps the verifier, who made it and the scope; the token itself is nowhere in the state
        var record = app.Services.GetRequiredService<ConsoleJoins>().Tokens.All().Single();
        Assert.Equal(((string)answer["tokenId"]!, adminId, "Ada", JoinTokens.VerifierFor(token)), (record.Id, record.MadeBy, record.MadeByName, record.Verifier));
        Assert.InRange(record.ExpiresAt - record.MadeAt, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));

        using var listed = await Send(HttpMethod.Get, "/api/join-tokens", admin);
        var listText = await listed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.DoesNotContain(token, listText);
        Assert.DoesNotContain(record.Verifier, listText);
        var waiting = Assert.Single(JsonNode.Parse(listText)!.AsArray())!;
        Assert.Equal((record.Id, "runner", "night", "Ada"), ((string)waiting["id"]!, (string)waiting["product"]!, (string?)waiting["group"], (string)waiting["madeBy"]!));

        var invite = Assert.Single(Audit(ConsoleJoins.InviteVerb));
        Assert.Equal((adminId, record.Id), (invite.Entry.ActorId, invite.Entry.TargetId));
        Assert.DoesNotContain(token, invite.Entry.Detail);
        // the request's own audit line would say nothing the token's line does not: the route is left to the joins
        Assert.Empty(Audit("console.write"));
    }

    [Fact]
    public async Task Without_an_address_the_command_names_the_console_s_public_address_and_a_loopback_address_is_flagged()
    {
        var (admin, _) = Session(UserRole.Admin, "Ada");
        var plain = await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"gateway"}"""), HttpStatusCode.OK);
        Assert.StartsWith("dbdatagateway pair --host https://console.example.com:7800 --token ", (string)plain["command"]!);
        Assert.Equal(["admin"], plain["roles"]!.AsArray().Select(r => (string)r!));
        Assert.False((bool)plain["loopback"]!);

        var local = await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"workspace","url":"https://localhost:7800"}"""), HttpStatusCode.OK);
        Assert.StartsWith("dbdataworkspace pair --host https://localhost:7800 ", (string)local["command"]!);
        Assert.True((bool)local["loopback"]!);
    }

    [Theory]
    [InlineData("""{"product":"store"}""", "a Gateway, a Runner or a Workspace")]
    [InlineData("""{"product":"runner","roles":["chief"]}""", "is not a role ceiling")]
    [InlineData("""{"product":"runner","minutes":2000}""", "from 1 to 1440 minutes")]
    [InlineData("""{"product":"runner","group":"no spaces"}""", "the group is letters")]
    [InlineData("""{"product":"runner","url":"http://console:7800"}""", "it is https://, a host and a port")]
    [InlineData("""{"product":"runner","url":"https://console:7800/console"}""", "it is https://, a host and a port")]
    public async Task What_cannot_be_made_is_refused_in_words_and_makes_no_token(string body, string words)
    {
        var (admin, _) = Session(UserRole.Admin, "Ada");
        var refused = await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, body), HttpStatusCode.BadRequest);
        Assert.Contains(words, (string)refused["error"]!);
        Assert.Empty(app.Services.GetRequiredService<ConsoleJoins>().Tokens.All());
    }

    [Fact]
    public async Task Cancel_ends_a_waiting_token_and_is_audited_and_a_spent_or_unknown_token_is_not_cancelled()
    {
        var (admin, adminId) = Session(UserRole.Admin, "Ada");
        var joins = app.Services.GetRequiredService<ConsoleJoins>();
        var first = (string)(await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"runner"}"""), HttpStatusCode.OK))["tokenId"]!;
        var second = (string)(await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"gateway"}"""), HttpStatusCode.OK))["tokenId"]!;

        using (var cancelled = await Send(HttpMethod.Delete, $"/api/join-tokens/{first}", admin)) Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Equal(JoinTokenState.Cancelled, joins.Tokens.Find(first)!.StateAt(DateTimeOffset.UtcNow));
        var line = Assert.Single(Audit(ConsoleJoins.CancelVerb));
        Assert.Equal((adminId, first, "ok"), (line.Entry.ActorId, line.Entry.TargetId, line.Entry.Outcome));
        Assert.Equal([second], (await Json(await Send(HttpMethod.Get, "/api/join-tokens", admin), HttpStatusCode.OK)).AsArray().Select(t => (string)t!["id"]!));

        // cancelling again changes nothing and records nothing more
        using (var again = await Send(HttpMethod.Delete, $"/api/join-tokens/{first}", admin)) Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Single(Audit(ConsoleJoins.CancelVerb));

        joins.Tokens.Update(second, t => t with { SpentAt = DateTimeOffset.UtcNow });
        Assert.Contains("already used", (string)(await Json(await Send(HttpMethod.Delete, $"/api/join-tokens/{second}", admin), HttpStatusCode.Conflict))["error"]!);
        Assert.Null(joins.Tokens.Find(second)!.CancelledAt);
        await Json(await Send(HttpMethod.Delete, "/api/join-tokens/jt-0000000000000000", admin), HttpStatusCode.NotFound);
        Assert.Single(Audit(ConsoleJoins.CancelVerb));
    }

    [Fact]
    public async Task A_viewer_can_neither_make_list_nor_cancel_a_token()
    {
        var (admin, _) = Session(UserRole.Admin, "Ada");
        var (viewer, _) = Session(UserRole.Viewer, "Vic");
        var id = (string)(await Json(await Send(HttpMethod.Post, "/api/join-tokens", admin, """{"product":"runner"}"""), HttpStatusCode.OK))["tokenId"]!;

        using (var make = await Send(HttpMethod.Post, "/api/join-tokens", viewer, """{"product":"runner"}""")) Assert.Equal(HttpStatusCode.Forbidden, make.StatusCode);
        using (var list = await Send(HttpMethod.Get, "/api/join-tokens", viewer)) Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        using (var cancel = await Send(HttpMethod.Delete, $"/api/join-tokens/{id}", viewer)) Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        var joins = app.Services.GetRequiredService<ConsoleJoins>();
        Assert.Single(joins.Tokens.All());
        Assert.Equal(JoinTokenState.Waiting, joins.Tokens.Find(id)!.StateAt(DateTimeOffset.UtcNow));
    }
}
