using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>The console host (plan 13, U07): a real HTTPS server over a temporary directory, answering the way the web app needs: who am I, the first-run invitation, and the users behind a session.</summary>
public sealed class ConsoleHostTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string webRoot = Path.Combine(Path.GetTempPath(), "ddk-console-web-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> log = [];
    private WebApplication app = null!;
    private HttpClient http = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<html><body>the app</body></html>");
        File.WriteAllText(Path.Combine(webRoot, "app.js"), "// code");
        Directory.CreateDirectory(Path.Combine(webRoot, "docs", "widgets"));
        File.WriteAllText(Path.Combine(webRoot, "docs", "widgets", "overview.md"), "# Widgets");
        var settings = new ConsoleSettings(directory, new Uri("https://localhost:0"));
        app = ConsoleHost.Build(settings, ConsoleHost.Certificate(directory), log.Add, webRoot);
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        foreach (var d in new[] { directory, webRoot }) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? session = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return request;
    }

    private string SessionFor(UserRole role)
    {
        var users = app.Services.GetRequiredService<UserStore>();
        return app.Services.GetRequiredService<SessionStore>().Create(users.CreateUser(role + " person", null, role).Id).Id;
    }

    [Fact]
    public async Task Health_and_version_answer_without_signing_in_and_name_the_product()
    {
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        var version = JsonNode.Parse(await http.GetStringAsync("/version"))!;
        Assert.Equal("dbdatakit", (string)version["product"]!);
        Assert.Equal(ConsoleHost.Version, (string)version["version"]!);
    }

    [Theory]
    [InlineData("/users")]
    [InlineData("/servers/sync-0123/replications/orders/mappings/dbo.Orders")]
    public async Task A_deep_link_of_the_app_returns_the_app_even_when_a_server_s_own_name_has_a_dot_in_it(string path)
    {
        var page = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("the app", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_module_s_docs_are_served_as_markdown_to_anyone_and_their_routes_are_the_app()
    {
        var page = await http.GetAsync("/docs/widgets/overview.md");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("text/markdown", page.Content.Headers.ContentType?.ToString());
        Assert.Equal("# Widgets", await page.Content.ReadAsStringAsync());
        // The viewer's own address for the same page is the app, not a file.
        Assert.Contains("the app", await http.GetStringAsync("/docs/widgets/overview"));
    }

    [Theory]
    [InlineData("/missing.js")]
    [InlineData("/assets/missing.css")]
    public async Task A_missing_file_is_not_the_app(string path)
    {
        var page = await http.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("the app", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_first_start_writes_the_first_run_invitation_and_the_invitation_is_good()
    {
        var text = File.ReadAllText(Path.Combine(directory, "FIRST-RUN.txt"));
        Assert.Contains("dbdatakit user invite", text);
        var code = text.Split("/invite#")[1].Split('\n')[0].Trim();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/api/invites/check?code={code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/invites/check?code=not-a-code")).StatusCode);
    }

    [Fact]
    public async Task The_passkey_options_are_spelt_the_way_a_browser_needs_them_whatever_enum_converter_the_host_has()
    {
        var code = File.ReadAllText(Path.Combine(directory, "FIRST-RUN.txt")).Split("/invite#")[1].Split('\n')[0].Trim();
        var begun = await http.PostAsJsonAsync("/api/invites/begin-registration", new { code, displayName = "Ada", email = (string?)null });
        Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
        var options = JsonNode.Parse(await begun.Content.ReadAsStringAsync())!;
        // WebAuthn spells these "public-key" and "none"; a host-wide string-enum converter spelt them with the C# names, and navigator.credentials.create refused the options.
        Assert.Equal("public-key", (string)options["pubKeyCredParams"]![0]!["type"]!);
        Assert.Equal("none", (string)options["attestation"]!);
        Assert.NotNull(options["challenge"]);

        var assertion = await http.PostAsync("/api/auth/passkey/begin", null);
        Assert.Equal(HttpStatusCode.OK, assertion.StatusCode);
        Assert.NotNull(JsonNode.Parse(await assertion.Content.ReadAsStringAsync())!["challenge"]);
    }

    [Fact]
    public async Task Who_am_I_is_open_and_says_how_to_sign_in_and_everything_else_is_closed()
    {
        var status = JsonNode.Parse(await http.GetStringAsync("/api/auth/status"))!;
        Assert.False((bool)status["authenticated"]!);
        Assert.Contains("passkeys", status["methods"]!.AsArray().Select(m => (string)m!));

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/invites")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request(HttpMethod.Get, "/api/users", session: "forged"))).StatusCode);
    }

    [Fact]
    public async Task An_administrator_sees_the_users_a_viewer_does_not()
    {
        var admin = SessionFor(UserRole.Admin);
        var viewer = SessionFor(UserRole.Viewer);

        var list = await http.SendAsync(Request(HttpMethod.Get, "/api/users", admin));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var users = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
        Assert.Equal(["Admin person", "Viewer person"], users.Select(u => (string)u!["displayName"]!).Order(StringComparer.Ordinal));

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Request(HttpMethod.Get, "/api/users", viewer))).StatusCode);

        var me = JsonNode.Parse(await (await http.SendAsync(Request(HttpMethod.Get, "/api/auth/status", admin))).Content.ReadAsStringAsync())!;
        Assert.True((bool)me["authenticated"]!);
        Assert.Equal("Admin", (string)me["role"]!);
    }

    [Fact]
    public async Task An_administrator_makes_an_invitation_and_signing_out_ends_the_session()
    {
        var admin = SessionFor(UserRole.Admin);
        var made = new HttpRequestMessage(HttpMethod.Post, "/api/invites") { Content = JsonContent.Create(new { role = "Viewer" }) };
        made.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={admin}");
        var created = await http.SendAsync(made);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var url = (string)JsonNode.Parse(await created.Content.ReadAsStringAsync())!["url"]!;
        Assert.Contains("/invite#", url);

        Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(Request(HttpMethod.Post, "/api/auth/sign-out", admin))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Request(HttpMethod.Get, "/api/users", admin))).StatusCode);
    }

    [Fact]
    public async Task The_web_app_is_served_to_anyone_and_deep_links_return_it_but_an_unknown_api_route_is_a_404()
    {
        Assert.Contains("the app", await http.GetStringAsync("/"));
        Assert.Contains("// code", await http.GetStringAsync("/app.js"));
        Assert.Contains("the app", await http.GetStringAsync("/invite"));
        Assert.Contains("the app", await http.GetStringAsync("/users"));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/nothing-here")).StatusCode);
    }
}

public sealed class ConsolePublicAddressTests
{
    [Fact]
    public async Task A_passkey_is_bound_to_the_public_address_not_to_the_address_the_console_listens_on()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), new Uri("https://console.example:8480")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
            await using (app)
            {
                await app.StartAsync();
                var passkeys = app.Services.GetRequiredService<DbDataKit.Web.Auth.PasskeyOptions>();
                Assert.Equal("console.example", passkeys.RelyingPartyId);
                Assert.Contains("https://console.example:8480", passkeys.Origins);
                await app.StopAsync();
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}

public sealed class ConsoleWithoutWebAppTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;

    public async ValueTask InitializeAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Without_the_web_app_the_console_says_so_instead_of_failing()
    {
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { BaseAddress = new Uri(app.Urls.First()) };
        Assert.Contains("web app is not installed", await http.GetStringAsync("/"));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/auth/status")).StatusCode);
    }
}
