using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>The servers a console manages (plan 13, U12): paired through the console's API with a real pairing exchange, listed with how each answers, forgotten.</summary>
public sealed class ConsoleInstancesTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string SessionFor(UserRole role) =>
        app.Services.GetRequiredService<SessionStore>().Create(app.Services.GetRequiredService<UserStore>().CreateUser(role + " person", null, role).Id).Id;

    private HttpRequestMessage Request(HttpMethod method, string path, string session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task An_administrator_pairs_a_server_and_sees_how_it_is()
    {
        var admin = SessionFor(UserRole.Admin);
        var paired = await http.SendAsync(Request(HttpMethod.Post, "/api/instances", admin, new { url = server.Url.AbsoluteUri, code = server.NewCode(), name = "Test server" }));
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var summary = JsonNode.Parse(await paired.Content.ReadAsStringAsync())!;
        Assert.Equal("Test server", (string)summary["name"]!);
        Assert.Equal(PairableServer.InstanceId, (string)summary["id"]!);
        Assert.Equal("fake-server", (string)summary["product"]!);
        Assert.Equal("9.9.9", (string)summary["version"]!);
        Assert.True((bool)summary["reachable"]!);
        Assert.True((bool)summary["ready"]!);

        // the server now lists this console among its paired clients, and the console pinned the certificate it saw
        Assert.Equal("dbdatakit console", Assert.Single(server.Clients.All()).Name);
        var listed = JsonNode.Parse(await http.GetStringAsync("/api/instances", admin))!.AsArray();
        Assert.Equal(PairableServer.InstanceId, (string)Assert.Single(listed)!["id"]!);
        Assert.Equal(server.Fingerprint, app.Services.GetRequiredService<InstanceRegistry>().All().Single().CertificateFingerprint);
    }

    [Fact]
    public async Task A_wrong_code_a_plain_http_address_and_a_server_that_does_not_answer_are_refused_with_words()
    {
        var admin = SessionFor(UserRole.Admin);
        var wrong = await http.SendAsync(Request(HttpMethod.Post, "/api/instances", admin, new { url = server.Url.AbsoluteUri, code = "AAAAA-BBBBB-CCCCC-DDDDD" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("code", (string)JsonNode.Parse(await wrong.Content.ReadAsStringAsync())!["error"]!);

        var plain = await http.SendAsync(Request(HttpMethod.Post, "/api/instances", admin, new { url = "http://example.test", code = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.Contains("https", (string)JsonNode.Parse(await plain.Content.ReadAsStringAsync())!["error"]!);

        var nothing = await http.SendAsync(Request(HttpMethod.Post, "/api/instances", admin, new { url = "https://127.0.0.1:1", code = server.NewCode() }));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);

        Assert.Empty(app.Services.GetRequiredService<InstanceRegistry>().All());
    }

    [Fact]
    public async Task Only_an_administrator_may_see_pair_or_forget_a_server()
    {
        var viewer = SessionFor(UserRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Get, "/api/instances", viewer))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Post, "/api/instances", viewer, new { url = server.Url.AbsoluteUri, code = server.NewCode() }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Delete, "/api/instances/x", viewer))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/instances")).StatusCode);
    }

    [Fact]
    public async Task A_server_that_stops_answering_is_listed_as_unreachable_and_can_be_forgotten()
    {
        var admin = SessionFor(UserRole.Admin);
        await http.SendAsync(Request(HttpMethod.Post, "/api/instances", admin, new { url = server.Url.AbsoluteUri, code = server.NewCode() }));
        await server.StopAsync();

        var listed = JsonNode.Parse(await http.GetStringAsync("/api/instances", admin))!.AsArray();
        var row = Assert.Single(listed)!;
        Assert.False((bool)row["reachable"]!);
        Assert.Equal("did not answer", (string)row["problem"]!);

        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Delete, $"/api/instances/{PairableServer.InstanceId}", admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(Request(HttpMethod.Delete, $"/api/instances/{PairableServer.InstanceId}", admin))).StatusCode);
        Assert.Empty(JsonNode.Parse(await http.GetStringAsync("/api/instances", admin))!.AsArray());
    }
}

internal static class HttpClientExtensions
{
    public static async Task<string> GetStringAsync(this HttpClient http, string path, string session)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        using var response = await http.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }
}
