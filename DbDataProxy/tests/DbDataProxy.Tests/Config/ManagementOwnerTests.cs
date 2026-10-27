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
}
