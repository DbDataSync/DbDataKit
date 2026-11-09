using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Tests;

/// <summary>What the console shows of upgrades (plan 24a UC08): the feed read with a mirror setting, the versions per product, each paired server's current and newest, and the mark for a release outside N and N-1.</summary>
public sealed class ConsoleUpgradesTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-" + Guid.NewGuid().ToString("N")[..8]);
    private string Mirror => Path.Combine(directory, "mirror");
    private WebApplication app = null!;
    private HttpClient http = null!;
    private PairableServer server = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync();
        Directory.CreateDirectory(directory);
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

    private string Admin() => app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser("An admin", null, UserRole.Admin).Id).Id;

    private async Task<HttpResponseMessage> GetAsync(string path, string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        return await http.SendAsync(request);
    }

    private async Task PairAsync(string session)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances") { Content = JsonContent.Create(new { url = server.Url.AbsoluteUri, code = server.NewCode(), name = "Test server" }) };
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
    }

    private void UseMirror() => File.WriteAllText(Path.Combine(directory, "settings.yaml"), $"upgrade:\n  feed:\n    mirror: {Mirror.Replace('\\', '/')}\n");

    private void Release(string version, int? generation = null, string product = "fake-server")
    {
        var folder = Path.Combine(Mirror, product, "v" + version);
        Directory.CreateDirectory(folder);
        var zip = Path.Combine(folder, $"{product}-v{version}-linux-x64.zip");
        File.WriteAllText(zip, "zip " + version);
        File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zip))) + " *" + Path.GetFileName(zip) + "\n");
        if (generation is { } g)
            File.WriteAllText(Path.Combine(folder, "release.json"), $"{{\"schema\":\"release/1\",\"product\":\"{product}\",\"version\":\"{version}\",\"channel\":\"stable\",\"schemaVersions\":{{\"generation\":{g}}}}}");
    }

    [Fact]
    public async Task A_mirror_folder_works_offline_and_the_server_is_shown_with_its_current_and_newest_version()
    {
        UseMirror();
        Release("2026.11.1.1000");
        Release("2026.11.9.1430", SuiteCompatibility.Generation);
        var admin = Admin();
        await PairAsync(admin);

        var response = await GetAsync("/api/upgrades/available", admin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.True((bool)answer["mirror"]!);
        Assert.Equal(SuiteCompatibility.Generation, (int)answer["consoleGeneration"]!);
        var product = Assert.Single(answer["products"]!.AsArray())!;
        Assert.Equal("fake-server", (string)product["product"]!);
        Assert.Equal(["2026.11.9.1430", "2026.11.1.1000"], product["releases"]!.AsArray().Select(r => (string)r!["version"]!));
        var row = Assert.Single(answer["servers"]!.AsArray())!;
        Assert.Equal("9.9.9", (string)row["version"]!);
        Assert.Equal("2026.11.9.1430", (string)row["newest"]!);
        Assert.True((bool)row["newerAvailable"]!);
        Assert.Null(row["outsideAfterRelease"]);
    }

    [Fact]
    public async Task A_release_built_outside_N_and_N_minus_1_of_the_console_is_marked_with_the_words()
    {
        UseMirror();
        var far = SuiteCompatibility.Generation + 3;
        Release("2026.11.9.1430", far);
        var admin = Admin();
        await PairAsync(admin);

        var row = Assert.Single(JsonNode.Parse(await (await GetAsync("/api/upgrades/available", admin)).Content.ReadAsStringAsync())!["servers"]!.AsArray())!;
        Assert.True((bool)row["newerAvailable"]!);
        var mark = (string)row["outsideAfterRelease"]!;
        Assert.Contains($"generation {far}", mark);
        Assert.EndsWith("Upgrade this console.", mark);
    }

    [Fact]
    public async Task A_server_on_the_newest_release_has_nothing_newer()
    {
        UseMirror();
        Release("9.9.9");
        var admin = Admin();
        await PairAsync(admin);
        var row = Assert.Single(JsonNode.Parse(await (await GetAsync("/api/upgrades/available", admin)).Content.ReadAsStringAsync())!["servers"]!.AsArray())!;
        Assert.False((bool)row["newerAvailable"]!);
    }

    [Fact]
    public async Task A_malformed_feed_is_refused_naming_the_file_and_a_malformed_setting_names_its_file_and_line()
    {
        UseMirror();
        Directory.CreateDirectory(Mirror);
        var file = Path.Combine(Mirror, "releases.json");
        File.WriteAllText(file, "{ not a list");
        var admin = Admin();
        await PairAsync(admin);

        var refused = await GetAsync("/api/upgrades/available", admin);
        Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode);
        Assert.Contains(file, (string)JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["error"]!);

        var settings = Path.Combine(directory, "settings.yaml");
        File.WriteAllText(settings, "upgrade:\n  feed:\n    mirrror: x\n");
        var bad = await GetAsync("/api/upgrades/available", admin);
        var message = (string)JsonNode.Parse(await bad.Content.ReadAsStringAsync())!["error"]!;
        Assert.Contains(settings, message);
        Assert.Contains("line 3", message);
    }

    [Fact]
    public async Task A_person_who_is_not_signed_in_is_refused()
    {
        var response = await http.GetAsync("/api/upgrades/available");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    }
}
