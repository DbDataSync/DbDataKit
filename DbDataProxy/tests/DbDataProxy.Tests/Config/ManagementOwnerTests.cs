using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Hosting.Local;
using DbDataKit.Web.Trust;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Tests.Config;

public sealed partial class ManagementTests
{
    private readonly ConsoleIdentity build1 = new("build-1", ConsoleSigningKey.Generate());
    private readonly ConsoleIdentity build2 = new("build-2", ConsoleSigningKey.Generate());

    /// <summary>Pairs a server client (a Build server) whose ceiling is Publisher.</summary>
    private async Task<PairedServer> PairPublisherAsync(ConsoleIdentity identity) =>
        await PairingClient.PairAsync(ManagementUrl, Files.Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code, identity, ClientKind.Server, identity.Id);

    private async Task<HttpResponseMessage> CallAsAsync(ConsoleIdentity identity, PairedServer server, HttpMethod method, string path, ServerRole roles, string? body = null)
    {
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(server.InstanceId, "ada", "Ada", roles)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static string Plain(string name) => Conn.Yaml(name, "duckdb", "Data Source=:memory:");

    [Fact]
    public async Task An_administrator_has_the_proxy_make_a_publisher_code_that_pairs_a_server_once_and_a_publisher_cannot_make_one()
    {
        await StartAsync();
        var admin = await PairAsync();

        var made = await CallAsync(admin, HttpMethod.Post, "/api/v1/pairing-codes", ServerRole.Admin, "{\"name\":\"build\",\"minutes\":5}");
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var code = (string)JsonNode.Parse(await made.Content.ReadAsStringAsync())!["code"]!;

        var publisher = await PairingClient.PairAsync(ManagementUrl, code, build1, ClientKind.Server, build1.Id);
        Assert.Equal(ServerRole.Publisher, publisher.Ceiling);
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(ManagementUrl, code, build2, ClientKind.Server, build2.Id));   // single use

        // a publisher may not make codes (only an administrator, who asked through a console)
        var refused = await CallAsAsync(build1, publisher, HttpMethod.Post, "/api/v1/pairing-codes", ServerRole.Publisher);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_publishing_client_creates_data_sources_stamped_with_its_id_and_changes_and_removes_only_those()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);

        var created = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/semantic", ServerRole.Publisher, Put(Plain("semantic"), null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var version = (string)JsonNode.Parse(await created.Content.ReadAsStringAsync())!["version"]!;
        Assert.Contains("managed_by: build-1", File.ReadAllText(Path.Combine(ConfigDirectory, "connections", "semantic.yaml")));
        Assert.True(Options.CurrentValue.DataSources.ContainsKey("semantic"));            // applied at once, like an administrator's

        // an administrator's source, made by hand, is not seen by the client
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        var listed = JsonNode.Parse(await (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Publisher)).Content.ReadAsStringAsync())!["data_sources"]!.AsArray();
        Assert.Equal(["semantic"], listed.Select(d => (string)d!["name"]!).ToList());
        Assert.Equal("build-1", (string)listed[0]!["managed_by"]!);

        var text = (string)JsonNode.Parse(await (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources/semantic", ServerRole.Publisher)).Content.ReadAsStringAsync())!["text"]!;
        var changed = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/semantic", ServerRole.Publisher, Put(text.Replace("schema: connection/1", "schema: connection/1\n# updated"), version));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var newVersion = (string)JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["version"]!;

        var removed = await CallAsAsync(build1, publisher, HttpMethod.Delete, $"/api/v1/data-sources/semantic?expected_version={newVersion}", ServerRole.Publisher);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "semantic.yaml")));
        Assert.True(File.Exists(Path.Combine(ConfigDirectory, "connections", "by-hand.yaml")));
    }

    [Fact]
    public async Task A_publishing_client_cannot_touch_what_was_made_by_hand_or_by_another_client_nor_the_serving_policy()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/theirs", ServerRole.Publisher, Put(Plain("theirs"), null));
        string Before(string name) => File.ReadAllText(Path.Combine(ConfigDirectory, "connections", name + ".yaml"));
        var byHand = Before("by-hand");
        var theirs = Before("theirs");

        foreach (var name in new[] { "by-hand", "theirs" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Get, $"/api/v1/data-sources/{name}", ServerRole.Publisher)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Put, $"/api/v1/data-sources/{name}", ServerRole.Publisher, Put(Plain(name), "0000"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Delete, $"/api/v1/data-sources/{name}", ServerRole.Publisher)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Post, $"/api/v1/data-sources/{name}/test", ServerRole.Publisher)).StatusCode);
        }
        Assert.Equal(byHand, Before("by-hand"));
        Assert.Equal(theirs, Before("theirs"));

        // the serving policy is an administrator's, even in a source the client makes; and a file cannot claim another owner
        var policy = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/mine", ServerRole.Publisher, Put(Conn.Yaml("mine", "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true"]), null));
        Assert.Equal(HttpStatusCode.Forbidden, policy.StatusCode);
        Assert.Contains("extensions.proxy", await policy.Content.ReadAsStringAsync());
        var claim = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/data-sources/mine", ServerRole.Publisher, Put(Plain("mine") + "managed_by: build-2\n", null));
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
        Assert.False(File.Exists(Path.Combine(ConfigDirectory, "connections", "mine.yaml")));

        // a token asking for more than the pairing allows (a viewer on a Publisher pairing) is not accepted at all, and nothing else of the API is the client's
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/config", ServerRole.Publisher)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/identities", ServerRole.Publisher)).StatusCode);
    }

    [Fact]
    public async Task The_audit_names_the_person_and_the_client_that_acted_for_them_and_an_administrator_is_not_limited_by_owner()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/semantic", ServerRole.Publisher, Put(Plain("semantic"), null));
        await CallAsAsync(build1, publisher, HttpMethod.Delete, "/api/v1/data-sources/by-hand", ServerRole.Publisher);   // refused: there is none by that name

        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(audit, a => (string)a["target"]! == "semantic" && (string)a["outcome"]! == "created" && (string)a["userId"]! == "ada" && (string)a["consoleId"]! == "build-1");

        // an administrator changes a client's source without owning it
        var read = JsonNode.Parse(await (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/semantic", ServerRole.Admin)).Content.ReadAsStringAsync())!;
        var changed = await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/semantic", ServerRole.Admin, Put((string)read["text"]! + "\n# reviewed\n", (string)read["version"]!));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
    }

    private string ConnectionFile(string name) => Path.Combine(ConfigDirectory, "connections", name + ".yaml");

    private static string AdoptBody(string client) => JsonSerializer(new { client });

    [Fact]
    public async Task An_administrator_adopts_a_hand_made_data_source_for_a_client_which_then_manages_it_and_the_audit_names_the_person()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        Assert.DoesNotContain("managed_by", File.ReadAllText(ConnectionFile("by-hand")));
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Publisher)).StatusCode);   // before: not the client's

        var adopted = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/by-hand/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.OK, adopted.StatusCode);
        var answer = JsonNode.Parse(await adopted.Content.ReadAsStringAsync())!;
        Assert.True((bool)answer["adopted"]!);
        Assert.Equal("build-1", (string)answer["managed_by"]!);
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionFile("by-hand")));

        // after: the client reads, changes and removes it, like one it made
        var read = JsonNode.Parse(await (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Publisher)).Content.ReadAsStringAsync())!;
        var changed = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Publisher, Put((string)read["text"]! + "# changed by the client\n", (string)read["version"]!));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var again = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/by-hand/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.False((bool)JsonNode.Parse(await again.Content.ReadAsStringAsync())!["adopted"]!);   // adopting what is already the client's changes nothing

        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && (string)a["outcome"]! == "adopted by build-1" && (string)a["userId"]! == "ada");
    }

    [Fact]
    public async Task A_server_client_cannot_adopt_whatever_its_ceiling_and_nothing_is_written()
    {
        await StartAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        var before = File.ReadAllText(ConnectionFile("by-hand"));

        var refused = await CallAsAsync(build1, publisher, HttpMethod.Post, "/api/v1/data-sources/by-hand/adopt", ServerRole.Publisher, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("person's act", await refused.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllText(ConnectionFile("by-hand")));

        // a server client with the admin role in its own ceiling is a server client still
        var strong = await PairingClient.PairAsync(ManagementUrl, Files.Codes().Create(ServerRoles.Close(ServerRole.Admin)).Code, build2, ClientKind.Server, build2.Id);
        var alsoRefused = await CallAsAsync(build2, strong, HttpMethod.Post, "/api/v1/data-sources/by-hand/adopt", ServerRole.Admin, AdoptBody("build-2"));
        Assert.Equal(HttpStatusCode.Forbidden, alsoRefused.StatusCode);
        Assert.Equal(before, File.ReadAllText(ConnectionFile("by-hand")));

        // a person with less than the admin role is refused too
        var lesser = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/by-hand/adopt", ServerRole.Viewer, AdoptBody("build-1"));
        Assert.True(lesser.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, lesser.StatusCode.ToString());
        Assert.Equal(before, File.ReadAllText(ConnectionFile("by-hand")));

        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["outcome"]! == "refused" && (string)a["consoleId"]! == "build-1" && (string)a["userId"]! == "ada");
    }

    [Fact]
    public async Task An_administrator_can_hand_a_data_source_of_one_client_to_another_and_cannot_adopt_for_a_client_that_is_not_paired_as_a_server()
    {
        await StartAsync();
        var admin = await PairAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/data-sources/theirs", ServerRole.Publisher, Put(Plain("theirs"), null));
        Assert.Contains("managed_by: build-2", File.ReadAllText(ConnectionFile("theirs")));

        var handed = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/theirs/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.OK, handed.StatusCode);
        Assert.Equal("build-2", (string)JsonNode.Parse(await handed.Content.ReadAsStringAsync())!["previous_owner"]!);
        var text = File.ReadAllText(ConnectionFile("theirs"));
        Assert.Contains("managed_by: build-1", text);
        Assert.DoesNotContain("build-2", text);                                         // the owner line is replaced, not doubled
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/data-sources/theirs", ServerRole.Publisher)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/data-sources/theirs", ServerRole.Publisher)).StatusCode);

        // the console is a person's client, not one that publishes; nobody by that id; no such source; no body
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/theirs/adopt", ServerRole.Admin, AdoptBody(console.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/theirs/adopt", ServerRole.Admin, AdoptBody("nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/none/adopt", ServerRole.Admin, AdoptBody("build-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/theirs/adopt", ServerRole.Admin, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await CallAsync(admin, HttpMethod.Get, "/api/v1/data-sources/theirs/adopt", ServerRole.Admin)).StatusCode);
    }

    [Fact]
    public async Task A_data_source_with_a_serving_policy_is_not_adopted_because_the_clients_write_would_drop_the_policy()
    {
        await StartAsync();
        var admin = await PairAsync();
        await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/limited", ServerRole.Admin, Put(Conn.Yaml("limited", "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true"]), null));
        var before = File.ReadAllText(ConnectionFile("limited"));

        var refused = await CallAsync(admin, HttpMethod.Post, "/api/v1/data-sources/limited/adopt", ServerRole.Admin, AdoptBody("build-1"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("extensions.proxy", await refused.Content.ReadAsStringAsync());
        Assert.Equal(before, File.ReadAllText(ConnectionFile("limited")));
    }

    private static string SecretBody(string value) => JsonSerializer(new { value });

    [Fact]
    public async Task A_publishing_client_stores_its_secrets_under_its_own_name_and_they_are_never_read_back_or_seen_by_another_client()
    {
        const string password = "SENTINEL-warehouse-pw-5d1e";
        await StartAsync();
        var first = await PairPublisherAsync(build1);
        var second = await PairPublisherAsync(build2);

        var stored = await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/secrets/warehouse", ServerRole.Publisher, SecretBody(password));
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var answer = await stored.Content.ReadAsStringAsync();
        Assert.Equal("secret:dbdata:proxy:managed-build-1:warehouse", (string)JsonNode.Parse(answer)!["reference"]!);
        Assert.DoesNotContain(password, answer);

        string Names(string body) => string.Join(",", JsonNode.Parse(body)!["secrets"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("warehouse", Names(await (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/secrets", ServerRole.Publisher)).Content.ReadAsStringAsync()));
        Assert.Equal("", Names(await (await CallAsAsync(build2, second, HttpMethod.Get, "/api/v1/secrets", ServerRole.Publisher)).Content.ReadAsStringAsync()));

        // the same name from another client is another secret, and removing it does not touch the first's
        await CallAsAsync(build2, second, HttpMethod.Put, "/api/v1/secrets/warehouse", ServerRole.Publisher, SecretBody("another"));
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build2, second, HttpMethod.Delete, "/api/v1/secrets/warehouse", ServerRole.Publisher)).StatusCode);
        Assert.Equal("warehouse", Names(await (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/secrets", ServerRole.Publisher)).Content.ReadAsStringAsync()));

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/secrets/..%2Fx", ServerRole.Publisher, SecretBody("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsAsync(build1, first, HttpMethod.Put, "/api/v1/secrets/empty", ServerRole.Publisher, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await CallAsAsync(build1, first, HttpMethod.Get, "/api/v1/secrets/warehouse", ServerRole.Publisher)).StatusCode);

        // the value is in the encrypted store and nowhere that is written in clear
        var audit = File.ReadAllText(Path.Combine(dataDirectory, "management-audit.log"));
        Assert.Contains("secret write", audit);
        Assert.DoesNotContain(password, audit);
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain(password, File.ReadAllText(file));
    }

    [Fact]
    public async Task A_data_source_a_publisher_makes_can_use_the_secret_it_stored_without_a_restart()
    {
        await StartAsync();
        var publisher = await PairPublisherAsync(build1);
        var reference = (string)JsonNode.Parse(await (await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/secrets/connection", ServerRole.Publisher, SecretBody("Data Source=:memory:"))).Content.ReadAsStringAsync())!["reference"]!;

        var text = $"schema: connection/1\nname: viaSecret\nengine: duckdb\nconnection_string_secret: '{reference}'\n";
        var created = await CallAsAsync(build1, publisher, HttpMethod.Put, "/api/v1/data-sources/viaSecret", ServerRole.Publisher, Put(text, null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var catalog = app.Services.GetRequiredService<DataSourceCatalog>();
        await using var result = await catalog.Resolve(Anyone, "viaSecret").Executor.ExecuteQueryAsync(new QueryRequest("SELECT 42 AS answer"), CancellationToken.None);
        Assert.Single(result.Schema.FieldsList);
    }
}
