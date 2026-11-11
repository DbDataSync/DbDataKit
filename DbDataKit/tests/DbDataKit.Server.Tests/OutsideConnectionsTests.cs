using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a IX01 on a real console: the connections of outside systems it keeps (<c>connections/&lt;name&gt;.yml</c>, kind <c>http</c> or <c>model-provider</c>): the list says each one's kind, address and sign-in, a file of an unknown kind or a database is refused naming
/// the file, a secret is a reference (a typed one is kept in the secret store), and a canary secret is in no answer, no file and no audit row.
/// </summary>
public sealed class OutsideConnectionsTests : IAsyncLifetime
{
    private const string Canary = "canary-secret-7f3a91c2-do-not-log";

    private const string Crm = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  name: X-Api-Key\n  key_secret: secret:dbdata:console:connection:crm:key\n";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-conns-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> bodies = [];
    private WebApplication app = null!;
    private HttpClient http = null!;
    private string admin = "";
    private string viewer = "";

    public async ValueTask InitializeAsync()
    {
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0")), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
        admin = SignIn("Ada", UserRole.Admin);
        viewer = SignIn("Vic", UserRole.Viewer);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private string SignIn(string name, UserRole role) =>
        app.Services.GetRequiredService<ISessionStore>().Create(app.Services.GetRequiredService<IUserAccounts>().CreateUser(name, null, role).Id).Id;

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, string? session, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (session is not null) request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={session}");
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        bodies.Add(text);
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null);
    }

    [Fact]
    public async Task A_connection_of_kind_http_is_saved_listed_with_its_kind_and_its_secret_is_kept_in_the_store_never_in_the_file()
    {
        Assert.Empty((await Call(HttpMethod.Get, "/api/outside-connections", admin)).Body!["connections"]!.AsArray());

        // a key written as itself is refused and the refusal does not repeat it
        var (refused, bad) = await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(Crm.Replace("secret:dbdata:console:connection:crm:key", Canary)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused);
        Assert.Contains("secret reference", (string)bad!["diagnostics"]![0]!["message"]!);
        Assert.DoesNotContain(Canary, bad.ToJsonString());
        Assert.False(File.Exists(Path.Combine(directory, "connections", "crm.yml")));

        // only an administrator writes
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/outside-connections/crm", viewer, new YamlBody(Crm))).Status);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(Crm))).Status);

        // the list (for a viewer too) says the kind, the address, the sign-in, and that the secret is not set
        var (_, listed) = await Call(HttpMethod.Get, "/api/outside-connections", viewer);
        var row = listed!["connections"]![0]!;
        Assert.Equal(("crm", "http", "https://api.example.com/v1", "api_key"), ((string)row["name"]!, (string)row["kind"]!, (string)row["endpoint"]!, (string)row["signIn"]!));
        Assert.False((bool)row["secrets"]![0]!["available"]!);
        Assert.Contains("is not set", (string)row["secrets"]![0]!["problem"]!);

        // the typed secret is kept in the store; the file still holds only the reference
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/key", viewer, new SecretBody(Canary))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/nope", admin, new SecretBody(Canary))).Status);
        var (stored, answer) = await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/key", admin, new SecretBody(Canary));
        Assert.Equal(HttpStatusCode.OK, stored);
        Assert.Equal("secret:dbdata:console:connection:crm:key", (string)answer!["reference"]!);
        var (_, after) = await Call(HttpMethod.Get, "/api/outside-connections", admin);
        Assert.True((bool)after!["connections"]![0]!["secrets"]![0]!["available"]!);
        Assert.Equal(Crm, File.ReadAllText(Path.Combine(directory, "connections", "crm.yml")));

        // the canary: in no answer, no file of the console but the secret store, no row of the record
        var (_, audit) = await Call(HttpMethod.Get, "/api/agents/audit?limit=1000", admin);
        Assert.Contains(audit!.AsArray(), a => (string)a!["kind"]! == "connection-secret-set");
        Assert.DoesNotContain(Canary, audit.ToJsonString());
        Assert.DoesNotContain(Canary, string.Join('\n', bodies));
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("secrets.yaml", StringComparison.Ordinal) && !f.Contains("keys") && !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-")))
            Assert.DoesNotContain(Canary, await File.ReadAllTextAsync(file));

        Assert.Equal(HttpStatusCode.NoContent, (await Call(HttpMethod.Delete, "/api/outside-connections/crm", admin)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Delete, "/api/outside-connections/crm", admin)).Status);
    }

    [Fact]
    public async Task A_file_of_an_unknown_kind_and_a_database_are_refused_naming_the_file_and_a_file_in_the_folder_that_does_not_read_is_listed_with_its_fault()
    {
        var (status, unknown) = await Call(HttpMethod.Put, "/api/outside-connections/odd", admin, new YamlBody("schema: connection/2\nkind: ftp\nname: odd\nendpoint: https://a.example.com\n"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("connections/odd.yml", (string)unknown!["diagnostics"]![0]!["message"]!);
        Assert.Contains("`ftp`", (string)unknown["diagnostics"]![0]!["message"]!);

        var (dbStatus, database) = await Call(HttpMethod.Put, "/api/outside-connections/wh", admin, new YamlBody("schema: connection/2\nname: wh\nengine: postgres\naddress: { host: h }\n"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dbStatus);
        Assert.Contains("outside systems", (string)database!["diagnostics"]![0]!["message"]!);

        var (nameStatus, named) = await Call(HttpMethod.Put, "/api/outside-connections/other", admin, new YamlBody(Crm));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, nameStatus);
        Assert.Contains("named other.yml", (string)named!["diagnostics"]![0]!["message"]!);

        // a file put there by hand that does not read shows in the list, with the fault
        Directory.CreateDirectory(Path.Combine(directory, "connections"));
        File.WriteAllText(Path.Combine(directory, "connections", "hand.yml"), "schema: connection/2\nkind: http\nname: hand\nendpoint: https://u:p@a.example.com\n");
        var (_, listed) = await Call(HttpMethod.Get, "/api/outside-connections", admin);
        var row = listed!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "hand")!;
        Assert.Null(row["kind"]);
        Assert.Contains("no user name, password or query", (string)row["diagnostics"]![0]!["message"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/outside-connections/..%2Fx", admin, new YamlBody(Crm))).Status);
    }

    [Fact]
    public async Task A_template_per_kind_is_a_file_that_reads_and_a_viewer_is_not_given_one()
    {
        foreach (var kind in new[] { "http", "model-provider" })
        {
            var (status, template) = await Call(HttpMethod.Get, $"/api/outside-connections/template?kind={kind}&name=starter", admin);
            Assert.Equal(HttpStatusCode.OK, status);
            var yaml = (string)template!["yaml"]!;
            Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Post, "/api/outside-connections/starter/check", admin, new YamlBody(yaml))).Status);
            var (saved, answer) = await Call(HttpMethod.Put, "/api/outside-connections/starter", admin, new YamlBody(yaml));
            Assert.True(saved == HttpStatusCode.OK, answer?.ToJsonString());
            Assert.Contains("kind: " + kind, File.ReadAllText(Path.Combine(directory, "connections", "starter.yml")));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/outside-connections/template?kind=http&name=x", viewer)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Get, "/api/outside-connections/template?kind=database&name=x", admin)).Status);
    }
}
