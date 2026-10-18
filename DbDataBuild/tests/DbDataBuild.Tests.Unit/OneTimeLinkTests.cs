using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Cli.Web;

namespace DbDataBuild.Tests.Unit;

/// <summary>A link to the page works once and turns into the session of the browser that opened it (`show` in a host that cannot render the app).</summary>
public sealed class OneTimeLinkTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-link-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly WebServer server;
    private readonly CookieContainer cookies = new();
    private readonly HttpClient browser;

    public OneTimeLinkTests()
    {
        Assert.Equal(0, CliApp.Run(["project", "create", "starter", dir, "--format", "json"], new StringWriter(), new StringWriter()));
        server = new WebServer(dir, new TuiCommand.CliHost(_ => null), 0, false, oneTimeLinks: true);
        server.StartAsync();
        browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies, UseCookies = true });
    }

    public void Dispose() { server.Dispose(); browser.Dispose(); try { Directory.Delete(dir, true); } catch (IOException) { } }

    [Fact]
    public async Task A_link_opens_once_and_the_browser_that_opened_it_has_the_session()
    {
        var link = server.NewLink("plans");
        var first = await browser.GetAsync(link);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/?screen=plans", first.Headers.Location!.ToString());              // the link is not left in the address
        Assert.Contains(first.Headers.GetValues("Set-Cookie"), c => c.Contains("HttpOnly") && c.Contains("SameSite=Strict"));

        var page = await browser.GetAsync($"http://127.0.0.1:{server.Port}/?screen=plans");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var session = Regex.Match(html, "name=\"ddb-token\" content=\"([0-9a-f]+)\"").Groups[1].Value;
        Assert.NotEmpty(session);
        Assert.DoesNotContain(Regex.Match(link, "token=([0-9a-f]+)").Groups[1].Value, html);       // the session is not the link
        var api = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/capabilities");
        api.Headers.Add("X-DDB-Token", session);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(api)).StatusCode);

        // the same link again, from anywhere, opens nothing
        using var other = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(link)).StatusCode);
    }

    [Fact]
    public async Task Without_a_session_the_page_and_the_api_are_closed_and_the_link_is_no_session()
    {
        var link = server.NewLink();
        var linkToken = Regex.Match(link, "token=([0-9a-f]+)").Groups[1].Value;
        using var stranger = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"http://127.0.0.1:{server.Port}/")).StatusCode);
        var api = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/api/capabilities");
        api.Headers.Add("X-DDB-Token", linkToken);                                         // a link used as a session token
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.SendAsync(api)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync($"http://127.0.0.1:{server.Port}/?token=0000")).StatusCode);
    }

    [Fact]
    public async Task Every_link_makes_its_own_session()
    {
        await browser.GetAsync(server.NewLink());
        using var second = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer(), UseCookies = true });
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync(server.NewLink())).StatusCode);
        string Session(string html) => Regex.Match(html, "name=\"ddb-token\" content=\"([0-9a-f]+)\"").Groups[1].Value;
        var a = Session(await (await browser.GetAsync($"http://127.0.0.1:{server.Port}/")).Content.ReadAsStringAsync());
        var b = Session(await (await second.GetAsync($"http://127.0.0.1:{server.Port}/")).Content.ReadAsStringAsync());
        Assert.NotEqual(a, b);
    }
}
