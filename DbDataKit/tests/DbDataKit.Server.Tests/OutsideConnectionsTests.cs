using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// Plan 27a IX01 on a real console: the connections of outside systems it keeps (<c>connections/&lt;name&gt;.yml</c>, kind <c>http</c> or <c>model-provider</c>): the list says each one's kind, address and sign-in, a file of an unknown kind or a database is refused naming
/// the file, a secret is a reference (a typed one is kept in the secret store), and a canary secret is in no answer, no file and no audit row.
/// </summary>
public sealed class OutsideConnectionsTests : IAsyncLifetime
{
    private const string Canary = "canary-secret-7f3a91c2-do-not-log";

    private const string Crm = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  name: X-Api-Key\n  key_secret: secret:dbdatakit:server:connection:crm:key\n";

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
        var (refused, bad) = await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(Crm.Replace("secret:dbdatakit:server:connection:crm:key", Canary)));
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
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/9bad", admin, new SecretBody(Canary))).Status);
        var (stored, answer) = await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/key", admin, new SecretBody(Canary));
        Assert.Equal(HttpStatusCode.OK, stored);
        Assert.Equal("secret:dbdatakit:server:connection:crm:key", (string)answer!["reference"]!);
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
        Assert.Contains("user name or password", (string)row["diagnostics"]![0]!["message"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Put, "/api/outside-connections/..%2Fx", admin, new YamlBody(Crm))).Status);
    }

    [Fact]
    public async Task A_file_server_connection_is_listed_with_its_address_keeps_its_private_key_as_a_secret_and_a_server_that_is_not_there_is_not_reached()
    {
        var listening = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listening.Start();
        var port = ((System.Net.IPEndPoint)listening.LocalEndpoint).Port;
        listening.Stop();   // a port nothing listens on
        var yaml = $"schema: connection/2\nkind: sftp\nname: drop\nfile_server:\n  host: 127.0.0.1\n  port: {port}\n  root: /upload\n  host_keys: [\"SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"]\nsign_in:\n  method: ssh_key\n  user: robot\n  private_key_secret: secret:dbdatakit:server:connection:drop:private_key\n";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/drop", admin, new YamlBody(yaml))).Status);

        var (_, listed) = await Call(HttpMethod.Get, "/api/outside-connections", viewer);
        var row = listed!["connections"]![0]!;
        Assert.Equal(($"sftp://127.0.0.1:{port}/upload", "ssh_key", "sftp"), ((string)row["endpoint"]!, (string)row["signIn"]!, (string)row["kind"]!));
        Assert.False((bool)row["secrets"]![0]!["available"]!);
        Assert.Equal("private_key", (string)row["secrets"]![0]!["field"]!);

        // the key is typed once and kept in the store; it is in no answer, no file and no row of the record
        var (stored, _) = await Call(HttpMethod.Put, "/api/outside-connections/drop/secrets/private_key", admin, new SecretBody("-----BEGIN OPENSSH PRIVATE KEY-----\n" + Canary + "\n-----END OPENSSH PRIVATE KEY-----"));
        Assert.Equal(HttpStatusCode.OK, stored);

        // Test: the key is not a key, so the sign-in is not even tried; either way the words say what to do and the answer holds no secret
        var (_, test) = await Call(HttpMethod.Post, "/api/outside-connections/drop/test", admin);
        Assert.False((bool)test!["ok"]!);
        Assert.False(string.IsNullOrWhiteSpace((string)test["message"]!));
        Assert.DoesNotContain(Canary, string.Join('\n', bodies));
        var (_, audit) = await Call(HttpMethod.Get, "/api/agents/audit?limit=1000", admin);
        Assert.DoesNotContain(Canary, audit!.ToJsonString());
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("secrets.yaml", StringComparison.Ordinal) && !f.Contains("keys") && !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-")))
            Assert.DoesNotContain(Canary, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task A_template_per_kind_is_a_file_that_reads_and_a_viewer_is_not_given_one()
    {
        foreach (var kind in new[] { "http", "model-provider", "sftp", "scp", "ftps", "smb", "s3", "azure-blob", "report-catalog" })
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

    [Fact]
    public async Task Test_of_an_api_connection_says_whether_the_system_was_reached_and_the_sign_in_accepted_and_holds_no_secret()
    {
        // a local API that wants the key in X-Api-Key
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.Run(context =>
        {
            context.Response.StatusCode = context.Request.Headers["X-Api-Key"] == Canary ? 200 : 401;
            return Task.CompletedTask;
        });
        await api.StartAsync();
        var address = api.Urls.First();
        var file = Crm.Replace("https://api.example.com/v1", address);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/crm", admin, new YamlBody(file))).Status);

        // the key is not set: nothing was sent, and the answer names the reference
        var (_, unset) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.False((bool)unset!["ok"]!);
        Assert.Contains("secret:dbdatakit:server:connection:crm:key", (string)unset["message"]!);
        Assert.Equal("not checked", (string)unset["signIn"]!);

        // a wrong key: the system answers and refuses it
        await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/key", admin, new SecretBody("a-wrong-key-canary"));
        var (_, refused) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.False((bool)refused!["ok"]!);
        Assert.True((bool)refused["reached"]!);
        Assert.Equal(("rejected", "Login", false), ((string)refused["signIn"]!, (string)refused["kind"]!, (bool)refused["temporary"]!));

        // the right key: reached and accepted
        await Call(HttpMethod.Put, "/api/outside-connections/crm/secrets/key", admin, new SecretBody(Canary));
        var (_, accepted) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.True((bool)accepted!["ok"]!);
        Assert.Equal("accepted", (string)accepted["signIn"]!);

        // a system that is not there
        await api.StopAsync();
        var (_, gone) = await Call(HttpMethod.Post, "/api/outside-connections/crm/test", admin);
        Assert.False((bool)gone!["ok"]!);
        Assert.False((bool)gone["reached"]!);
        Assert.True((bool)gone["temporary"]!);

        // only an administrator tests; an unknown name is not found; a model provider is tested per model
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Post, "/api/outside-connections/crm/test", viewer)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Post, "/api/outside-connections/nobody/test", admin)).Status);
        var provider = "schema: connection/2\nkind: model-provider\nname: p\nmodel_provider: { provider: ollama, models: [ { id: m } ] }\n";
        await Call(HttpMethod.Put, "/api/outside-connections/p", admin, new YamlBody(provider));
        var (_, perModel) = await Call(HttpMethod.Post, "/api/outside-connections/p/test", admin);
        Assert.Contains("Models screen", (string)perModel!["message"]!);

        // neither key is in any answer or in the record
        Assert.DoesNotContain(Canary, string.Join('\n', bodies));
        Assert.DoesNotContain("a-wrong-key-canary", string.Join('\n', bodies));
        var (_, audit) = await Call(HttpMethod.Get, "/api/agents/audit?limit=1000", admin);
        Assert.Contains(audit!.AsArray(), a => (string)a!["kind"]! == "connection-tested");
        Assert.DoesNotContain(Canary, audit.ToJsonString());
        Assert.DoesNotContain("a-wrong-key-canary", audit.ToJsonString());
    }

    // ---- webhooks and mail servers: the delivery channels as connections (plan 22a GP18 fold) ----

    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/CANARYslackTOKEN";

    [Fact]
    public async Task A_pasted_slack_address_is_split_into_a_template_and_secret_parts_that_are_kept_in_the_store_and_only_the_template_is_shown()
    {
        var (forbidden, _) = await Call(HttpMethod.Post, "/api/outside-connections/ops-slack/webhook", viewer, new WebhookBody(SlackUrl));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);

        var (status, split) = await Call(HttpMethod.Post, "/api/outside-connections/ops-slack/webhook", admin, new WebhookBody(SlackUrl));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("Slack", "slack", "https://hooks.slack.com/services/{token}"), (((string)split!["service"]!), (string)split["format"]!, (string)split["template"]!));
        var reference = (string)split["secrets"]!["token"]!;
        Assert.StartsWith("secret:", reference, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARYslackTOKEN", split.ToJsonString());                                       // the part is in no answer

        // the file the editor writes holds the template and the reference; the list shows the template, never the composed address
        var yaml = $"schema: connection/2\nkind: http\nname: ops-slack\nendpoint: https://hooks.slack.com/services/{{token}}\nendpoint_secrets:\n  token: {reference}\nwebhook:\n  format: slack\n";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/ops-slack", admin, new YamlBody(yaml))).Status);
        var (_, listed) = await Call(HttpMethod.Get, "/api/outside-connections", admin);
        var row = listed!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "ops-slack")!;
        Assert.Equal("https://hooks.slack.com/services/{token}", (string)row["endpoint"]!);
        Assert.True((bool)row["secrets"]![0]!["available"]!);
        Assert.Equal("token", (string)row["secrets"]![0]!["field"]!);
        Assert.DoesNotContain("CANARYslackTOKEN", string.Join('\n', bodies));
        Assert.DoesNotContain("CANARYslackTOKEN", await File.ReadAllTextAsync(Path.Combine(directory, "connections", "ops-slack.yml")));

        // Test says where to test it, and does not call the address
        var (_, test) = await Call(HttpMethod.Post, "/api/outside-connections/ops-slack/test", admin);
        Assert.False((bool)test!["ok"]!);
        Assert.Contains("Delivery page", (string)test["message"]!);

        // rotation: only the secret part is typed again; the file (the template) is untouched
        var before = await File.ReadAllTextAsync(Path.Combine(directory, "connections", "ops-slack.yml"));
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Put, "/api/outside-connections/ops-slack/secrets/token", admin, new SecretBody("T000/B000/ROTATEDtokenNEW"))).Status);
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(directory, "connections", "ops-slack.yml")));
        var audit = (await Call(HttpMethod.Get, "/api/agents/audit?limit=1000", admin)).Body!.ToJsonString();
        Assert.Contains("connection-webhook-split", audit);
        Assert.DoesNotContain("CANARYslackTOKEN", audit);
        Assert.DoesNotContain("ROTATEDtokenNEW", audit);
    }

    [Fact]
    public async Task An_address_the_editor_does_not_know_is_split_by_the_segments_and_query_values_the_person_marks()
    {
        const string url = "https://hooks.example.com/in/CANARYgenericPART/x?key=CANARYquery&page=2";
        var (unknown, ask) = await Call(HttpMethod.Post, "/api/outside-connections/gen/webhook", admin, new WebhookBody(url));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown);
        Assert.False((bool)ask!["detected"]!);
        Assert.Equal(3, (int)ask["pathSegments"]!);
        Assert.Equal(["key", "page"], ask["queryKeys"]!.AsArray().Select(k => (string)k!));
        Assert.DoesNotContain("CANARY", ask.ToJsonString());

        var (status, split) = await Call(HttpMethod.Post, "/api/outside-connections/gen/webhook", admin, new WebhookBody(url, [1], ["key"]));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://hooks.example.com/in/{seg2}/x?key={key}&page=2", (string)split!["template"]!);
        Assert.Equal(["key", "seg2"], split["secrets"]!.AsObject().Select(p => p.Key).Order());
        Assert.DoesNotContain("CANARY", split.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Post, "/api/outside-connections/gen/webhook", admin, new WebhookBody("ftp://x"))).Status);
    }

    [Fact]
    public async Task A_webhook_file_with_its_secret_in_plain_sight_is_refused_naming_the_part_and_the_templates_for_each_kind_save()
    {
        var (status, refused) = await Call(HttpMethod.Put, "/api/outside-connections/leak", admin, new YamlBody($"schema: connection/2\nkind: http\nname: leak\nendpoint: {SlackUrl}\nwebhook: {{ format: slack }}\n"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("part of the path after `/services/` is its secret", string.Join(" ", refused!["diagnostics"]!.AsArray().Select(d => (string)d!["message"]!)));
        Assert.DoesNotContain("CANARYslackTOKEN", refused.ToJsonString());
        Assert.False(File.Exists(Path.Combine(directory, "connections", "leak.yml")));

        foreach (var (kind, flavor) in new[] { ("smtp", (string?)null), ("http", "slack"), ("http", "teams"), ("http", "signed") })
        {
            var (ok, template) = await Call(HttpMethod.Get, $"/api/outside-connections/template?kind={kind}&name=t-{flavor ?? kind}" + (flavor is null ? "" : "&flavor=" + flavor), admin);
            Assert.Equal(HttpStatusCode.OK, ok);
            var yaml = (string)template!["yaml"]!;
            var (saved, result) = await Call(HttpMethod.Put, $"/api/outside-connections/t-{flavor ?? kind}", admin, new YamlBody(yaml));
            Assert.True(saved == HttpStatusCode.OK, yaml + "\n" + result);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Call(HttpMethod.Get, "/api/outside-connections/template?kind=smtp&name=x&flavor=slack", admin)).Status);
        var (_, all) = await Call(HttpMethod.Get, "/api/outside-connections", admin);
        var smtp = all!["connections"]!.AsArray().Single(c => (string)c!["name"]! == "t-smtp")!;
        Assert.Equal(("smtp", "smtp.example.com:587 (starttls)"), (((string)smtp["kind"]!), (string)smtp["endpoint"]!));
    }
}
