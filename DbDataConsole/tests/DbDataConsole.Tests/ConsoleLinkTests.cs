using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>The console brokers a pairing between two servers it manages (plan 13, T11): the proxy makes a publisher code, the build server pairs with it, as the signed-in person, and nobody types a code.</summary>
public sealed class ConsoleLinkTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<JsonNode> pairedWith = [];
    private readonly List<string> codeAskers = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer proxy = null!;
    private PairableServer build = null!;
    private PairableServer sync = null!;

    public async ValueTask InitializeAsync()
    {
        proxy = await PairableServer.StartAsync(product: "dbdataproxy", instanceId: "proxy-1", map: (server, codes, _) =>
            server.MapPost(PairingCodeIssuer.Path, (HttpContext context) =>
            {
                var token = context.ManagementToken();
                codeAskers.Add(token.SubjectName ?? token.SubjectId);
                var (status, body) = PairingCodeIssuer.Issue(codes, ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed, null);
                return Results.Json(body, statusCode: status);
            }));
        build = await PairableServer.StartAsync(product: "dbdatabuild", instanceId: "build-1", map: (server, _, _) =>
            server.MapPost("/api/v1/proxies", async (HttpContext context) =>
            {
                var body = (await context.Request.ReadFromJsonAsync<JsonObject>())!;
                if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "needs admin" }, statusCode: 403);
                pairedWith.Add(body);
                return Results.Json(new { name = (string?)body["name"] });
            }));
        sync = await PairableServer.StartAsync(product: "dbdatasync", instanceId: "sync-1");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var instances = app.Services.GetRequiredService<ConsoleInstances>();
        var every = ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver;
        await instances.PairAsync(proxy.Url, proxy.NewCodeWith(every), "Edge proxy", default);
        await instances.PairAsync(build.Url, build.NewCodeWith(every), "Warehouse build", default);
        await instances.PairAsync(sync.Url, sync.NewCodeWith(every), "Sync", default);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var s in new[] { proxy, build, sync }) await s.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private Task<HttpResponseMessage> Link(UserRole role, object body)
    {
        var session = app.Services.GetRequiredService<SessionStore>().Create(app.Services.GetRequiredService<UserStore>().CreateUser("Ana", null, role).Id).Id;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances/link") { Content = JsonContent.Create(body) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return http.SendAsync(request);
    }

    [Fact]
    public async Task An_administrator_pairs_a_build_server_with_a_proxy_and_no_code_is_typed()
    {
        var response = await Link(UserRole.Admin, new { from = "build-1", to = "proxy-1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paired = Assert.Single(pairedWith);
        Assert.Equal("edge-proxy", (string)paired["name"]!);                                   // the proxy's name, made into an id
        Assert.Equal(proxy.Url.AbsoluteUri.TrimEnd('/'), (string)paired["url"]!);
        Assert.Equal(["Ana"], codeAskers);                                                     // the proxy made the code for the signed-in person
        // the code the build server received is the one the proxy made: it works once, and carries the publisher ceiling and nothing else
        var grant = proxy.Codes.Redeem((string)paired["code"]!);
        Assert.Equal(ServerRole.Publisher, grant!.Ceiling);
    }

    [Fact]
    public async Task A_name_can_be_given_and_only_a_build_server_and_a_proxy_can_be_linked()
    {
        Assert.Equal(HttpStatusCode.OK, (await Link(UserRole.Admin, new { from = "build-1", to = "proxy-1", name = "reports" })).StatusCode);
        Assert.Equal("reports", (string)pairedWith.Single()["name"]!);

        foreach (var wrong in new[] { new { from = "sync-1", to = "proxy-1", name = (string?)null }, new { from = "build-1", to = "sync-1", name = (string?)null }, new { from = "proxy-1", to = "build-1", name = (string?)null }, new { from = "build-1", to = "build-1", name = (string?)null } })
            Assert.Equal(HttpStatusCode.BadRequest, (await Link(UserRole.Admin, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Link(UserRole.Admin, new { from = "build-1", to = "nobody" })).StatusCode);
        Assert.Single(pairedWith);
    }

    [Fact]
    public async Task A_viewer_cannot_link_servers()
    {
        var response = await Link(UserRole.Viewer, new { from = "build-1", to = "proxy-1" });
        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
        Assert.Empty(pairedWith);
        Assert.Empty(codeAskers);
    }
}
