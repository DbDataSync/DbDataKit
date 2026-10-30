using System.Net;
using DbDataSync.Api.Management;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DbDataSync.Api.Tests;

/// <summary>
/// What the server answers at an address that is not the API (plan 13, U11): it serves no web app any more, and says so.
/// <para>
/// Two things are held here. An unmatched <c>/api</c> or <c>/hubs</c> path is a plain 404, never a page of words: a client of the API that gets a sentence
/// back parses it as JSON and reports something incomprehensible, which looks like a client bug from every angle except this one. And any other address, the root above all,
/// names the console (and its address, when this server was told it) rather than answering a blank 404, because that is what a person with an old bookmark sees.
/// </para>
/// </summary>
public sealed class ApiFallbackTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task AnUnmatchedApiRoute_IsA404AndNotTheNotice()
    {
        var response = await _client.GetAsync("/api/there-is-no-such-endpoint");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("console", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The hub's own path is the same case: a client that gets words back from a negotiate request fails somewhere far away from the cause.</summary>
    [Fact]
    public async Task AnUnmatchedHubRoute_IsA404AndNotTheNotice()
    {
        var response = await _client.GetAsync("/hubs/not-a-hub");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("console", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheRoot_SaysTheWebAppIsTheConsolesAndHowToSayWhereItIs_WhenNoAddressWasGiven()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("no longer serves a web app", body);
        Assert.Contains("dbdatakit console", body);
        Assert.Contains(ConsoleNotice.ConfigurationKey, body);
        Assert.Contains(ConsoleNotice.EnvironmentVariable, body);
    }

    [Fact]
    public async Task TheRoot_ReturnsTheConsolesAddress_WhenTheServerWasToldIt()
    {
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { [ConsoleNotice.ConfigurationKey] = "https://console.example:8480" })));

        var response = await configured.CreateClient().GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("https://console.example:8480/", body);
        Assert.DoesNotContain(ConsoleNotice.ConfigurationKey, body);
    }

    /// <summary>An old bookmark of one of the screens (a replication's page, the docs) is told the same, with a 404: there is no such page here.</summary>
    [Theory]
    [InlineData("/replications/sales/mappings/orders")]
    [InlineData("/docs/install.md")]
    [InlineData("/invite")]
    [InlineData("/index.html")]
    public async Task AnOldScreenAddress_IsA404ThatNamesTheConsole(string path)
    {
        var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no longer serves a web app", body);
    }

    [Theory]
    [InlineData("https://console.example:8480", "https://console.example:8480/")]
    [InlineData("  http://localhost:8480/path ", "http://localhost:8480/path")]
    public void TheAddress_IsRead_AsGiven(string configured, string expected) =>
        Assert.Equal(expected, ConsoleNotice.Address(configured)?.AbsoluteUri);

    [Theory]
    [InlineData("console.example")]
    [InlineData("ftp://console.example")]
    public void AnAddressThatIsNotHttp_IsNotTakenForTheConsole(string configured) =>
        Assert.Null(ConsoleNotice.Address(configured));

    /// <summary>Still reachable, which is the thing the fallback must not have broken: the API, the version a console asks first, and health.</summary>
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/version")]
    public async Task TheApiAndTheManagementEndpoints_AreUnaffected(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(path)).StatusCode);
}
