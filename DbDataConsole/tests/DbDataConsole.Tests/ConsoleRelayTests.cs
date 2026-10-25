using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>The console's relay (plan 13, D16 and U08): a signed-in person's call, forwarded over the pin to a paired server with a token that names them.</summary>
public sealed class ConsoleRelayTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;
    private string admin = null!;
    private string viewer = null!;
    private string adminId = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync();
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        (admin, adminId) = Session(UserRole.Admin);
        (viewer, _) = Session(UserRole.Viewer);
        // Ceiling: what the server lets this console grant. A real pairing, so the server holds the console's key.
        await app.Services.GetRequiredService<ConsoleInstances>().PairAsync(server.Url, server.NewCodeWith(DbDataKit.Web.Trust.ServerRole.Admin | DbDataKit.Web.Trust.ServerRole.Operator | DbDataKit.Web.Trust.ServerRole.Author | DbDataKit.Web.Trust.ServerRole.Approver), "Fake", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private (string Session, string UserId) Session(UserRole role)
    {
        var user = app.Services.GetRequiredService<UserStore>().CreateUser(role + " person", null, role);
        return (app.Services.GetRequiredService<SessionStore>().Create(user.Id).Id, user.Id);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? session, string? body = null, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return http.SendAsync(request);
    }

    private static string Id => PairableServer.InstanceId;

    [Fact]
    public async Task A_call_reaches_the_server_with_a_token_that_names_the_person_and_their_roles()
    {
        var response = await Send(HttpMethod.Get, $"/relay/{Id}/api/echo?x=1", admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echo = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(adminId, (string)echo["subject"]!);
        Assert.Equal("Admin person", (string)echo["name"]!);
        Assert.Equal("dbdatakit console", (string)echo["client"]!);
        Assert.Contains("admin", echo["roles"]!.AsArray().Select(r => (string)r!));
        Assert.Equal("?x=1", (string)echo["query"]!);
    }

    [Fact]
    public async Task A_viewer_gets_the_viewer_role_and_the_servers_refusal_comes_through_as_it_is()
    {
        var echo = JsonNode.Parse(await (await Send(HttpMethod.Get, $"/relay/{Id}/api/echo", viewer)).Content.ReadAsStringAsync())!;
        Assert.Equal(["viewer"], echo["roles"]!.AsArray().Select(r => (string)r!).ToList());

        var refused = await Send(HttpMethod.Get, $"/relay/{Id}/api/admin-only", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("needs admin", await refused.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/relay/{Id}/api/admin-only", admin)).StatusCode);
    }

    [Fact]
    public async Task What_the_server_lets_the_console_grant_limits_what_an_administrator_holds_there()
    {
        // Pair again, this time as a server that lets this console grant reading only.
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        await instances.PairAsync(server.Url, server.NewCodeWith(DbDataKit.Web.Trust.ServerRole.Viewer), "Fake", default);
        Assert.Equal(DbDataKit.Web.Trust.ServerRoles.Close(DbDataKit.Web.Trust.ServerRole.Viewer), instances.Registry.Find(Id)!.Ceiling);

        var echo = JsonNode.Parse(await (await Send(HttpMethod.Get, $"/relay/{Id}/api/echo", admin)).Content.ReadAsStringAsync())!;
        Assert.Equal(["viewer"], echo["roles"]!.AsArray().Select(r => (string)r!).ToList());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/relay/{Id}/api/admin-only", admin)).StatusCode);
    }

    [Fact]
    public async Task A_body_and_its_type_are_forwarded_for_every_method()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            var echo = JsonNode.Parse(await (await Send(method, $"/relay/{Id}/api/echo", admin, """{"name":"x"}""")).Content.ReadAsStringAsync())!;
            Assert.Equal(method.Method, (string)echo["method"]!);
            Assert.Equal("""{"name":"x"}""", (string)echo["body"]!);
            Assert.StartsWith("application/json", (string)echo["contentType"]!);
        }
        Assert.Equal("DELETE", (string)JsonNode.Parse(await (await Send(HttpMethod.Delete, $"/relay/{Id}/api/echo", admin)).Content.ReadAsStringAsync())!["method"]!);
    }

    [Fact]
    public async Task Only_a_signed_in_person_may_relay_and_only_api_calls_to_a_registered_server_are_relayed()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/relay/{Id}/api/echo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(HttpMethod.Get, $"/relay/{Id}/api/echo", "forged")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/relay/nobody/api/echo", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"/relay/{Id}/health", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"/relay/{Id}/management/v1/pairing", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"/relay/{Id}/api/../health", admin)).StatusCode);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_a_502_that_names_it()
    {
        await server.StopAsync();
        var response = await Send(HttpMethod.Get, $"/relay/{Id}/api/echo", admin);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Fake did not answer", await response.Content.ReadAsStringAsync());
    }
}
