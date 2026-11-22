using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 13a, E05, with the real programs: two Gateways (each started from its built output), a console in this process speaking for a signed-in person. A data source of one Gateway is listed and copied to the other through the console,
/// and the target validates it as it would any file and keeps it in force; a second copy under the same name is a conflict, not an overwrite. What Playwright shows in the browser is these calls: the lists are the servers' own,
/// through the relay, and the copy is the console's. (A copy to or from a Sync server went with the Sync server, plan 33 SC23d; the console's refusal of a copy the target does not accept is <c>ConsoleConnectionsTests</c>'.)
/// </summary>
public sealed class ConnectionsAcrossServersTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-connections-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private WebApplication console = null!;
    private HttpClient http = null!;
    private string edgeId = null!;
    private string coreId = null!;
    private string personId = null!;
    private string edgeDirectory = null!;
    private string coreDirectory = null!;

    /// <summary>A Gateway of its own directory with a management listener, a certificate of its own and the data sources given (file name and text); its pairing code and the address of its management API.</summary>
    private async Task<(string Code, Uri Url, RealProcess Process)> StartGatewayAsync(string directory, IReadOnlyDictionary<string, string>? environment, params (string Name, string Text)[] sources)
    {
        var gateway = Programs.Gateway;
        Directory.CreateDirectory(Path.Combine(directory, "config", "connections"));
        Directory.CreateDirectory(Path.Combine(directory, "config", "gateway"));
        File.WriteAllText(Path.Combine(directory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        File.WriteAllText(Path.Combine(directory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        foreach (var (name, text) in sources) File.WriteAllText(Path.Combine(directory, "config", "connections", name + ".yaml"), text);
        var management = Programs.FreePort();
        var code = Programs.CodeIn(await RealProcess.RunAsync(gateway, "server", "pair", "--repo", directory, "--max-role", "admin"));
        var process = RealProcess.Start(gateway, ["server", "run", $"--Gateway:DataDirectory={directory}", $"--Gateway:Listen=https://127.0.0.1:{Programs.FreePort()}",
            "--Gateway:Management:Enabled=true", $"--Gateway:Management:Listen=https://127.0.0.1:{management}", "--Gateway:Management:LocalChannel=false"], environment: environment);
        started.Add(process);
        return (code, new Uri($"https://127.0.0.1:{management}"), process);
    }

    public async ValueTask InitializeAsync()
    {
        (edgeDirectory, coreDirectory) = (Path.Combine(root, "edge"), Path.Combine(root, "core"));
        var consoleDirectory = Path.Combine(root, "console");

        // two Gateways: the edge one has a data source made by hand, the core one has none
        var edge = await StartGatewayAsync(edgeDirectory, null, ("erp",
            "# the ERP reader\nschema: connection/1\nname: erp\nengine: sqlserver\naddress: { host: localhost, database: Erp }\nauth: { mode: sql, user: reader, password_secret: 'env:ERP_DB_PASSWORD' }\nextensions:\n  gateway:\n    allow_writes: false\n"));
        // the core one has the variable the data source's login names, so an administrator can bind it there (plan 30 PB02)
        var core = await StartGatewayAsync(coreDirectory, new Dictionary<string, string> { ["ERP_DB_PASSWORD"] = "not-a-real-password" });
        await Task.WhenAll(edge.Process.WaitUntilAsync(new Uri(edge.Url, "/health/live"), TimeSpan.FromSeconds(90)), core.Process.WaitUntilAsync(new Uri(core.Url, "/health/live"), TimeSpan.FromSeconds(90)));

        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        edgeId = (await instances.PairAsync(edge.Url, edge.Code, "Edge gateway")).InstanceId;
        coreId = (await instances.PairAsync(core.Url, core.Code, "Core gateway")).InstanceId;
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

    private static string GatewayFile(string directory, string name) => Path.Combine(directory, "config", "connections", name + ".yaml");

    [Fact]
    public async Task A_data_source_is_listed_and_copied_between_two_real_Gateways_and_the_target_validates_it()
    {
        // the lists, as the Connections page asks for them
        var edgeList = await AsPerson(HttpMethod.Get, $"/relay/{edgeId}/api/v1/data-sources");
        Assert.Equal(["erp"], edgeList.Body!["data_sources"]!.AsArray().Select(c => (string)c!["name"]!));
        var coreList = await AsPerson(HttpMethod.Get, $"/relay/{coreId}/api/v1/data-sources");
        Assert.Empty(coreList.Body!["data_sources"]!.AsArray());

        // the edge Gateway to the core one: the file the core has is the edge's file under its new name, with the Gateway's own settings and the secret reference as written, and the core has validated it (it is in force: listed and valid)
        // plan 30 PB02: the copy names a secret the core Gateway's production has not bound, so the core refuses it, naming the data source, the slot, the channel and how to bind it; nothing is written
        var refused = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = edgeId, name = "erp", to = coreId, newName = "erp-core" });
        Assert.True(refused.Status == HttpStatusCode.Conflict, $"copy: {refused.Status} {refused.Body}");
        Assert.Contains("data-source 'erp-core'", (string)refused.Body!["error"]!);
        Assert.Contains("env:ERP_DB_PASSWORD", (string)refused.Body!["error"]!);
        Assert.Contains("channel 'prod'", (string)refused.Body!["error"]!);
        Assert.False(File.Exists(GatewayFile(coreDirectory, "erp-core")));
        var bound = await AsPerson(HttpMethod.Put, $"/relay/{coreId}/api/v1/bindings/prod/data-source/erp-core", new { slots = new Dictionary<string, string> { ["env:ERP_DB_PASSWORD"] = "env:ERP_DB_PASSWORD" } });
        Assert.True(bound.Status == HttpStatusCode.OK, $"bind: {bound.Status} {bound.Body}");

        var copied = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = edgeId, name = "erp", to = coreId, newName = "erp-core" });
        Assert.True(copied.Status == HttpStatusCode.OK, $"copy: {copied.Status} {copied.Body}");
        var written = File.ReadAllText(GatewayFile(coreDirectory, "erp-core"));
        Assert.Contains("name: erp-core", written);
        Assert.Contains("# the ERP reader", written);                         // edited in place: the comments stay
        Assert.Contains("allow_writes: false", written);                      // extensions.gateway is the target's own
        Assert.Equal("env:ERP_DB_PASSWORD", DbDataKit.Connections.ConnectionDefinitionLoader.LoadDatabaseConnection(written, "erp-core.yaml").Require().Auth!.PasswordSecret);
        var listed = (await AsPerson(HttpMethod.Get, $"/relay/{coreId}/api/v1/data-sources")).Body!["data_sources"]!.AsArray().Single(d => (string)d!["name"]! == "erp-core")!;
        Assert.True((bool)listed["valid"]!);

        // the name is taken now: a second copy is a conflict, not an overwrite
        var again = await AsPerson(HttpMethod.Post, "/api/connections/copy", new { from = edgeId, name = "erp", to = coreId, newName = "erp-core" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(written, File.ReadAllText(GatewayFile(coreDirectory, "erp-core")));
        Assert.False(File.Exists(GatewayFile(edgeDirectory, "erp-core")));
    }
}
