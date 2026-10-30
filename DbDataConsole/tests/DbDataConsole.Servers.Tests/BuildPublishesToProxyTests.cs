using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataConsole.Servers.Tests;

/// <summary>
/// Plan 13a, T10's done-when and T12, with the real programs in one run: a Build server and a Proxy (each started from its built output, as <c>server run</c> starts it), and a console in this process that is paired with both and links them.
/// A person publishes a connection through the console; the Build server then creates and updates its own data source on the Proxy, is refused on one made by hand and on <c>extensions.proxy</c>, cannot adopt for itself, and an administrator
/// adopts for it; the Proxy's audit names the person and the Build server each time. One story, because starting the two servers is most of the cost.
/// </summary>
public sealed class BuildPublishesToProxyTests : IAsyncLifetime
{
    private const string ReadLogin = "Server=db.example;User Id=reader;Password=READ-pw-4c1e;Encrypt=True";
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private WebApplication console = null!;
    private HttpClient http = null!;
    private string proxyId = null!;
    private string buildId = null!;
    private string buildClientId = null!;
    private string person = null!;
    private string personId = null!;
    private string proxyDirectory = null!;
    private string buildDirectory = null!;
    private Uri proxyUrl = null!;
    private Uri buildUrl = null!;
    private string consoleDirectory = null!;

    private string ProxyConfig(string name) => Path.Combine(proxyDirectory, "config", "connections", name + ".yaml");

    public async ValueTask InitializeAsync()
    {
        var build = Programs.Build;
        var proxy = Programs.Proxy;
        (proxyDirectory, buildDirectory, consoleDirectory) = (Path.Combine(root, "proxy"), Path.Combine(root, "build"), Path.Combine(root, "console"));
        var project = Path.Combine(root, "sales");
        Directory.CreateDirectory(Path.Combine(proxyDirectory, "config", "connections"));
        Directory.CreateDirectory(Path.Combine(proxyDirectory, "config", "proxy"));
        Directory.CreateDirectory(buildDirectory);

        // the Proxy: its management listener on, a certificate of its own, and one data source made by hand (nothing needs to answer behind it)
        File.WriteAllText(Path.Combine(proxyDirectory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        File.WriteAllText(Path.Combine(proxyDirectory, "config", "proxy", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        File.WriteAllText(ProxyConfig("erp"), "schema: connection/1\nname: erp\nengine: sqlserver\naddress: { host: localhost, database: Erp }\nauth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }\n");
        var (flight, management) = (Programs.FreePort(), Programs.FreePort());
        var proxyCode = Programs.CodeIn(await RealProcess.RunAsync(proxy, "server", "pair", "--repo", proxyDirectory, "--max-role", "admin"));
        var proxyProcess = RealProcess.Start(proxy, ["server", "run", $"--Proxy:DataDirectory={proxyDirectory}", "--Proxy:Listen:Address=127.0.0.1", $"--Proxy:Listen:Port={flight}",
            "--Proxy:Management:Enabled=true", "--Proxy:Management:Address=127.0.0.1", $"--Proxy:Management:Port={management}", "--Proxy:Management:LocalChannel=false"]);
        started.Add(proxyProcess);
        proxyUrl = new Uri($"https://127.0.0.1:{management}");

        // the Build server: one project, the read login of its connection in the server's own store, every role but admin to a console
        await RealProcess.RunAsync(build, "project", "create", "starter", project);
        await RealProcess.RunAsync(build, "server", "project", "add", "starter", "--path", project, "--repo", buildDirectory);
        // an ODBC and a DuckDB connection too (read only, as those drivers are): every engine the Proxy serves can be published
        File.WriteAllText(Path.Combine(project, "connections", "lake.yaml"), "schema: connection/1\nname: lake\nengine: odbc\naccess: readonly\n");
        File.WriteAllText(Path.Combine(project, "connections", "local.yaml"), "schema: connection/1\nname: local\nengine: duckdb\naccess: readonly\n");
        await RealProcess.RunWithInputAsync(build, "Driver={ODBC Driver 18 for SQL Server};Server=db.example;UID=reader;PWD=ODBC-pw-77aa\n", "secret", "set", "dbdata:build:connection:lake:read", "--stdin", "--repo", buildDirectory);
        await RealProcess.RunWithInputAsync(build, "Data Source=/data/local.duckdb\n", "secret", "set", "dbdata:build:connection:local:read", "--stdin", "--repo", buildDirectory);
        await RealProcess.RunWithInputAsync(build, ReadLogin + "\n", "secret", "set", "dbdata:build:connection:sqlserver:read", "--stdin", "--repo", buildDirectory);
        var buildCode = Programs.CodeIn(await RealProcess.RunAsync(build, "server", "pair", "--repo", buildDirectory, "--max-role", "viewer,author,operator,approver,admin"));
        var buildPort = Programs.FreePort();
        buildUrl = new Uri($"https://127.0.0.1:{buildPort}");
        var buildProcess = RealProcess.Start(build, ["server", "run", "--repo", buildDirectory, "--listen", buildUrl.AbsoluteUri.TrimEnd('/')]);
        started.Add(buildProcess);
        await Task.WhenAll(proxyProcess.WaitUntilAsync(new Uri(proxyUrl, "/health/live"), TimeSpan.FromSeconds(90)), buildProcess.WaitUntilAsync(new Uri(buildUrl, "/health/live"), TimeSpan.FromSeconds(90)));

        // the console, here: paired with both, so it can speak for a signed-in person to each
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        proxyId = (await instances.PairAsync(proxyUrl, proxyCode, "Edge proxy")).InstanceId;
        buildId = (await instances.PairAsync(buildUrl, buildCode, "Sales build")).InstanceId;
        (person, personId) = ("Ada Admin", console.Services.GetRequiredService<UserStore>().CreateUser("Ada Admin", null, UserRole.Admin).Id);
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        foreach (var program in started) await program.DisposeAsync();
        try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string SessionFor(string userId) => console.Services.GetRequiredService<SessionStore>().Create(userId).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AsPerson(HttpMethod method, string path, object? body = null, string? userId = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={SessionFor(userId ?? personId)}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> Publish(string connection, string dataSource) =>
        AsPerson(HttpMethod.Post, $"/relay/{buildId}/api/v1/projects/starter/proxies/edge/publish", new { connection, data_source = dataSource });

    private List<JsonNode> Audit() =>
        [.. File.ReadAllLines(Path.Combine(proxyDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!)];

    private static bool Entry(JsonNode a, string command, string target, string outcome, string userId, string clientId) =>
        (string?)a["command"] == command && (string?)a["target"] == target && (string?)a["outcome"] == outcome && (string?)a["userId"] == userId && (string?)a["consoleId"] == clientId;

    [Fact]
    public async Task A_build_server_publishes_and_updates_its_own_data_source_on_a_proxy_and_is_refused_on_what_is_not_its_own_and_the_audit_names_the_person_and_the_server()
    {
        // 1. the console links the two (no code typed): the Proxy makes a publisher code, the Build server pairs with it, both as the signed-in person
        var linked = await AsPerson(HttpMethod.Post, "/api/instances/link", new { from = buildId, to = proxyId, name = "edge" });
        Assert.True(linked.Status == HttpStatusCode.OK, $"link: {linked.Status} {linked.Body}");
        var paired = (await AsPerson(HttpMethod.Get, $"/relay/{buildId}/api/v1/proxies")).Body!["proxies"]!.AsArray();
        Assert.Equal("edge", (string)paired.Single()!["name"]!);

        // the Build server's own client id, as the Proxy knows it: its identity is in its own secret store, where the program keeps it
        var buildStore = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(Path.GetFullPath(buildDirectory)), SecretKeys.Open(Path.GetFullPath(buildDirectory), new SecretKeyOptions(), null, null));
        var buildIdentity = await new ConsoleKeyStore(buildStore, buildStore).GetOrCreateAsync();
        buildClientId = buildIdentity.Id;

        // 2. creates its own data source: the file is the Proxy's, stamped with the Build server's id, and holds a reference to the secret and never the login
        var created = await Publish("sqlserver", "warehouse");
        Assert.True(created.Status == HttpStatusCode.OK, $"publish: {created.Status} {created.Body}");
        Assert.True((bool)created.Body!["created"]!);
        var file = File.ReadAllText(ProxyConfig("warehouse"));
        Assert.Contains($"managed_by: {buildClientId}", file);
        Assert.Contains("engine: sqlserver", file);
        Assert.Contains($"secret:dbdata:proxy:managed-{buildClientId}:warehouse-read", file);
        Assert.DoesNotContain("READ-pw-4c1e", file);
        foreach (var other in Directory.EnumerateFiles(proxyDirectory, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("READ-pw-4c1e", File.ReadAllText(other));

        // 2b. the other engines a Proxy serves: the Proxy accepts the files (it validates them as it would once written) and the logins stay out of them
        foreach (var (name, engine) in new[] { ("lake", "odbc"), ("local", "duckdb") })
        {
            var other = await Publish(name, name);
            Assert.True(other.Status == HttpStatusCode.OK, $"publish {name}: {other.Status} {other.Body}");
            Assert.Contains($"engine: {engine}", File.ReadAllText(ProxyConfig(name)));
            Assert.DoesNotContain("ODBC-pw-77aa", File.ReadAllText(ProxyConfig(name)));
        }

        // 3. updates it: the same call again is a change on the version it read, not a second source
        var updated = await Publish("sqlserver", "warehouse");
        Assert.True(updated.Status == HttpStatusCode.OK, $"publish again: {updated.Status} {updated.Body}");
        Assert.False((bool)updated.Body!["created"]!);

        // 4. refused on a data source made by hand: in the Proxy's words, with how a person settles it, and nothing is written
        var byHand = File.ReadAllText(ProxyConfig("erp"));
        var refused = await Publish("sqlserver", "erp");
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("not made by a publisher", (string)refused.Body!["error"]!);
        Assert.Equal(buildClientId, (string)refused.Body["adopt"]!["client"]!);
        Assert.Equal(byHand, File.ReadAllText(ProxyConfig("erp")));

        // 5. refused on extensions.proxy: Build never sends it, so the call is made with the Build server's own key, as a server client that tried; the person named is the one it acts for
        var policy = await CallProxyAsBuildServer(buildIdentity, HttpMethod.Put, "/api/v1/data-sources/warehouse", new
        {
            text = "schema: connection/1\nname: warehouse\nengine: sqlserver\nconnection_string: 'Server=s'\nextensions:\n  proxy:\n    allow_writes: true\n",
            expected_version = ProxyDataSourcesVersion(File.ReadAllText(ProxyConfig("warehouse"))),
        });
        Assert.Equal(HttpStatusCode.Forbidden, policy.Status);
        Assert.Contains("extensions.proxy", (string)policy.Body!["error"]!);
        Assert.DoesNotContain("allow_writes", File.ReadAllText(ProxyConfig("warehouse")));

        // 6. cannot adopt for itself, whatever it holds: a server client's act is refused, the file is the same
        var selfAdopt = await CallProxyAsBuildServer(buildIdentity, HttpMethod.Post, "/api/v1/data-sources/erp/adopt", new { client = buildClientId });
        Assert.Equal(HttpStatusCode.Forbidden, selfAdopt.Status);
        Assert.Equal(byHand, File.ReadAllText(ProxyConfig("erp")));

        // 7. a person who is not an administrator cannot adopt either; an administrator can, and then the Build server manages it
        var viewer = console.Services.GetRequiredService<UserStore>().CreateUser("Vic Viewer", null, UserRole.Viewer).Id;
        var viewerTries = await AsPerson(HttpMethod.Post, $"/relay/{proxyId}/api/v1/data-sources/erp/adopt", new { client = buildClientId }, viewer);
        Assert.Equal(HttpStatusCode.Forbidden, viewerTries.Status);
        Assert.Equal(byHand, File.ReadAllText(ProxyConfig("erp")));
        var adopted = await AsPerson(HttpMethod.Post, $"/relay/{proxyId}/api/v1/data-sources/erp/adopt", new { client = buildClientId });
        Assert.True(adopted.Status == HttpStatusCode.OK, $"adopt: {adopted.Status} {adopted.Body}");
        Assert.Contains($"managed_by: {buildClientId}", File.ReadAllText(ProxyConfig("erp")));
        var now = await Publish("sqlserver", "erp");
        Assert.True(now.Status == HttpStatusCode.OK, $"publish after adopt: {now.Status} {now.Body}");

        // 8. the Proxy's audit says who: the person, and the client that acted for them (the Build server), or the console for the person's own act
        var audit = Audit();
        var consoleId = (await console.Services.GetRequiredService<ConsoleKeyStore>().GetOrCreateAsync()).Id;
        Assert.NotEqual(buildClientId, consoleId);
        Assert.Contains(audit, a => Entry(a, "data-source adopt", "erp", $"adopted by {buildClientId}", personId, consoleId));   // the person's own act, through the console
        Assert.Contains(audit, a => Entry(a, "data-source write", "warehouse", "created", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "data-source write", "warehouse", "changed", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "secret write", "warehouse-read", "stored", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "data-source get", "erp", "refused", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "data-source write", "warehouse", "refused", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "data-source adopt", "erp", "refused", personId, buildClientId));
        Assert.Contains(audit, a => Entry(a, "data-source write", "erp", "changed", personId, buildClientId));
        Assert.DoesNotContain(audit, a => a.ToJsonString().Contains("READ-pw-4c1e"));
    }

    private static string ProxyDataSourcesVersion(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A call to the Proxy signed with the Build server's own key, as the Build server makes it, for the person who is signed in at the console.</summary>
    private async Task<(HttpStatusCode Status, JsonNode? Body)> CallProxyAsBuildServer(ConsoleIdentity identity, HttpMethod method, string path, object? body)
    {
        var instance = console.Services.GetRequiredService<ConsoleInstances>().Registry.Find(proxyId)!;
        using var client = new HttpClient(PairingClient.Pinned(instance.CertificateFingerprint)) { BaseAddress = proxyUrl };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(instance.InstanceId, personId, person, ServerRole.Publisher)));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }
}
