using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 13a, E05, with the real programs: a Sync server and a Gateway (each started from its built output), a console in this process speaking for a signed-in person. A connection made on Sync through the relay is listed, copied to the Gateway as a data source,
/// and the Gateway validates it as it would any file (a copy it does not support is refused with its diagnostic and nothing is written); a data source of the Gateway is copied to Sync, which stores the password the person typed under the copy's own reference
/// and never in the file. What Playwright shows in the browser is these calls: the lists are the servers' own, through the relay, and the copy is the console's.
/// </summary>
public sealed class ConnectionsAcrossServersTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-connections-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private WebApplication console = null!;
    private HttpClient http = null!;
    private string syncId = null!;
    private string gatewayId = null!;
    private string personId = null!;
    private string gatewayDirectory = null!;
    private string syncDirectory = null!;

    public async ValueTask InitializeAsync()
    {
        var (gateway, sync) = (Programs.Gateway, Programs.Sync);
        (gatewayDirectory, syncDirectory) = (Path.Combine(root, "gateway"), Path.Combine(root, "sync"));
        var consoleDirectory = Path.Combine(root, "console");
        Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "connections"));
        Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "gateway"));
        Directory.CreateDirectory(syncDirectory);

        // the Gateway: a management listener, a certificate of its own and one data source made by hand
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "connections", "erp.yaml"),
            "# the ERP reader\nschema: connection/1\nname: erp\nengine: sqlserver\naddress: { host: localhost, database: Erp }\nauth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }\nextensions:\n  gateway:\n    allow_writes: false\n");
        var management = Programs.FreePort();
        var gatewayCode = Programs.CodeIn(await RealProcess.RunAsync(gateway, "server", "pair", "--repo", gatewayDirectory, "--max-role", "admin"));
        var gatewayProcess = RealProcess.Start(gateway, ["server", "run", $"--Gateway:DataDirectory={gatewayDirectory}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={Programs.FreePort()}",
            "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={management}", "--Gateway:Management:LocalChannel=false"]);
        started.Add(gatewayProcess);
        var gatewayUrl = new Uri($"https://127.0.0.1:{management}");

        // the Sync server: https with a certificate made here (a server must present one to be paired with)
        var certificate = Path.Combine(root, "sync.pfx");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllBytes(certificate, made.Export(X509ContentType.Pfx, "servers-e2e"));
        }
        var syncCode = Programs.CodeIn(await RealProcess.RunAsync(sync, "server", "pair", "--repo", syncDirectory, "--max-role", "admin"));
        var syncUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
        var syncProcess = RealProcess.Start(sync, ["server", "run", "--repo", syncDirectory, "--url", syncUrl.AbsoluteUri.TrimEnd('/')], environment: new Dictionary<string, string>
        {
            ["Kestrel__Certificates__Default__Path"] = certificate, ["Kestrel__Certificates__Default__Password"] = "servers-e2e",
        });
        started.Add(syncProcess);
        await Task.WhenAll(gatewayProcess.WaitUntilAsync(new Uri(gatewayUrl, "/health/live"), TimeSpan.FromSeconds(90)), syncProcess.WaitUntilAsync(new Uri(syncUrl, "/api/health"), TimeSpan.FromSeconds(120)));

        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        gatewayId = (await instances.PairAsync(gatewayUrl, gatewayCode, "Edge gateway")).InstanceId;
        syncId = (await instances.PairAsync(syncUrl, syncCode, "Sync one")).InstanceId;
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        foreach (var program in started) await program.DisposeAsync();
        try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AsPerson(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private string GatewayFile(string name) => Path.Combine(gatewayDirectory, "config", "connections", name + ".yaml");

    [Fact]
    public async Task A_connection_is_listed_and_copied_between_a_real_Sync_server_and_a_real_Gateway_and_the_target_validates_it()
    {
        // a connection made on Sync as its screen makes it (through the relay, with the password typed once)
        var made = await AsPerson(HttpMethod.Put, $"/relay/{syncId}/api/connections/orders", new
        {
            name = "orders", driverType = "MsSql", host = "sql01", port = 1433, database = "Orders", authMode = "SqlAuth", userId = "svc_orders", password = "sync-typed-pw",
            testQuery = "SELECT 1",
        });
        Assert.True(made.Status == HttpStatusCode.OK, $"make: {made.Status} {made.Body}");

        // the lists, as the Connections page asks for them
        var syncList = await AsPerson(HttpMethod.Get, $"/relay/{syncId}/api/connections");
        Assert.Equal(["orders"], syncList.Body!.AsArray().Select(c => (string)c!["name"]!));
        var gatewayList = await AsPerson(HttpMethod.Get, $"/relay/{gatewayId}/api/v1/data-sources");
        Assert.Equal(["erp"], gatewayList.Body!["data_sources"]!.AsArray().Select(c => (string)c!["name"]!));

        // Sync to the Gateway: the file the Gateway has is the Sync file with its own settings left out, and the Gateway has validated it (it is in force: listed and valid)
        var toGateway = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = syncId, name = "orders", to = gatewayId, newName = "orders-edge" });
        Assert.True(toGateway.Status == HttpStatusCode.OK, $"copy to gateway: {toGateway.Status} {toGateway.Body}");
        var written = File.ReadAllText(GatewayFile("orders-edge"));
        Assert.Contains("name: orders-edge", written);
        Assert.DoesNotContain("test_query", written);                         // extensions.sync is Sync's
        Assert.DoesNotContain("sync-typed-pw", written);
        Assert.Equal("secret:dbdata:gateway:connection:orders-edge", DbDataKit.Connections.ConnectionDefinitionLoader.Load(written, "orders-edge.yaml").Require().Auth!.PasswordSecret);
        var listed = (await AsPerson(HttpMethod.Get, $"/relay/{gatewayId}/api/v1/data-sources")).Body!["data_sources"]!.AsArray().Single(d => (string)d!["name"]! == "orders-edge")!;
        Assert.True((bool)listed["valid"]!);

        // a copy the Gateway does not support is its refusal: its words and the line, and nothing is written
        var refused = await AsPerson(HttpMethod.Put, $"/relay/{syncId}/api/connections/events", new { name = "events", driverType = "Postgres", host = "pg01", port = 5432, database = "Events", authMode = "IntegratedAuth" });
        Assert.True(refused.Status == HttpStatusCode.OK, $"make events: {refused.Status} {refused.Body}");
        var pg = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = syncId, name = "events", to = gatewayId });
        Assert.NotEqual(HttpStatusCode.OK, pg.Status);
        Assert.Contains("did not accept the copy", (string)pg.Body!["error"]!);
        Assert.NotEmpty(pg.Body["diagnostics"]!.AsArray());
        Assert.False(File.Exists(GatewayFile("events")));

        // the name is taken now: a second copy is a conflict, not an overwrite
        var again = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = syncId, name = "orders", to = gatewayId, newName = "orders-edge" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(written, File.ReadAllText(GatewayFile("orders-edge")));

        // the Gateway to Sync: Sync reads the file with its own loader and stores the typed password under the copy's own reference
        var toSync = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = gatewayId, name = "erp", to = syncId, newName = "erp-sync", password = "typed-for-sync" });
        Assert.True(toSync.Status == HttpStatusCode.OK, $"copy to sync: {toSync.Status} {toSync.Body}");
        var copy = (await AsPerson(HttpMethod.Get, $"/relay/{syncId}/api/connections/erp-sync")).Body!;
        Assert.Equal("localhost", (string)copy["host"]!);
        Assert.Equal("reader", (string)copy["userId"]!);
        Assert.Equal("dbdata:sync:connection:erp-sync", (string)copy["credentialSecretRef"]!);
        var file = (await AsPerson(HttpMethod.Get, $"/relay/{syncId}/api/connections/erp-sync/file")).Body!;
        Assert.DoesNotContain("typed-for-sync", (string)file["text"]!);
        Assert.StartsWith("schema: connection/1", (string)file["text"]!);
        Assert.DoesNotContain("allow_writes", (string)file["text"]!);        // the Gateway's serving policy stays on the Gateway

        // without the typed password Sync says what it lacks, naming the reference, and keeps nothing
        var noPassword = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = gatewayId, name = "erp", to = syncId, newName = "erp-no-password" });
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.Status);
        Assert.Contains("dbdata:sync:connection:erp-no-password", (string)noPassword.Body!["error"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await AsPerson(HttpMethod.Get, $"/relay/{syncId}/api/connections/erp-no-password")).Status);
    }
}
