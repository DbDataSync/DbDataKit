using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.State.Api;
using DbDataKit.Web;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 30 PB02 and PB03 over HTTP: the shared publish routes on the management listener of a running Gateway, with the token check, the role each call needs, signatures and streamed uploads.</summary>
public sealed partial class ManagementTests
{
    private static string Menu(string title, string? signature = null) =>
        JsonSerializer(new { manifest = "{\"title\":\"" + title + "\",\"entries\":[]}", signature });

    // a console's token carries viewer or admin; a publisher is a paired server client with the publisher role, which is who the owner rules know
    private readonly ConsoleIdentity builder = new("builder-1", ConsoleSigningKey.Generate());
    private PairedServer? builderPairing;

    private async Task<PairedServer> PairBuilderAsync()
    {
        var code = Files.Codes().Create(ServerRoles.Close(ServerRole.Publisher)).Code;
        return builderPairing = await PairingClient.PairAsync(ManagementUrl, code, builder, ClientKind.Server, "builder");
    }

    private async Task<HttpResponseMessage> AsPublisherAsync(PairedServer server, HttpMethod method, string path, string? body = null)
    {
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint));
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, path));
        request.Headers.Add(ManagementAuthentication.TokenHeader, builder.Issuer().Issue(new ConsoleTokenRequest(builderPairing!.InstanceId, "builder", "builder", ServerRole.Publisher)));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> UploadAsync(PairedServer server, HttpMethod method, string sha, ServerRole roles, Stream? body = null, bool token = true)
    {
        var asBuilder = roles == ServerRole.Publisher;
        using var http = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint)) { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(method, new Uri(ManagementUrl, $"/api/v1/artifacts/{sha}"));
        if (token) request.Headers.Add(ManagementAuthentication.TokenHeader, (asBuilder ? builder : console).Issuer().Issue(new ConsoleTokenRequest(asBuilder ? builderPairing!.InstanceId : server.InstanceId, asBuilder ? "builder" : "ada", asBuilder ? "builder" : "Ada", roles)));
        if (body is not null) request.Content = new StreamContent(body);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task The_shared_definition_routes_need_a_token_and_the_role_each_call_needs()
    {
        await StartAsync();
        var server = await PairAsync();
        await PairBuilderAsync();

        using (var bare = new HttpClient(PairingClient.Pinned(server.CertificateFingerprint)))
            Assert.Equal(HttpStatusCode.Unauthorized, (await bare.GetAsync(new Uri(ManagementUrl, "/api/v1/definitions/app-nav"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(server, HttpMethod.Head, new string('a', 64), ServerRole.Viewer, token: false)).StatusCode);

        // signed versions are required in a group by default: an unsigned publish is refused (403) naming the kind, whoever publishes
        var unsigned = await CallAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/menu", ServerRole.Admin, Menu("one"));
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        Assert.Contains("requires signed versions of app-nav", await unsigned.Content.ReadAsStringAsync());

        // only an administrator trusts a key or sets the policy
        var key = ConsoleSigningKey.Generate();
        var keyBody = JsonSerializer(new { public_key = Convert.ToBase64String(key.PublicKey).TrimEnd('=').Replace('+', '-').Replace('/', '_'), label = "ci" });
        Assert.Equal(HttpStatusCode.Forbidden, (await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/trust/app-nav/keys", keyBody)).StatusCode);
        var added = await CallAsync(server, HttpMethod.Put, "/api/v1/trust/app-nav/keys", ServerRole.Admin, keyBody);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal(key.Fingerprint, (string)JsonNode.Parse(await added.Content.ReadAsStringAsync())!["key_id"]!);

        // a viewer reads and publishes nothing; a publisher publishes a signed version and changes no version that is served
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/menu", ServerRole.Viewer, Menu("one", Sign(key, "one")))).StatusCode);
        var published = await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/menu", Menu("one", Sign(key, "one")));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal("verified", (string)JsonNode.Parse(await published.Content.ReadAsStringAsync())!["signature"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await AsPublisherAsync(server, HttpMethod.Post, "/api/v1/definitions/app-nav/menu/rollback", "{}")).StatusCode);

        // a signature over other text does not hold, and the version the group serves stays
        var tampered = await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/menu", Menu("two", Sign(key, "one")));
        Assert.Equal(HttpStatusCode.Forbidden, tampered.StatusCode);
        var read = JsonNode.Parse(await (await CallAsync(server, HttpMethod.Get, "/api/v1/definitions/app-nav/menu", ServerRole.Viewer)).Content.ReadAsStringAsync())!;
        Assert.Equal(1, (int)read["channels"]![0]!["active"]!);
        Assert.Single(read["versions"]!.AsArray());

        // the group marked test needs none; the administrator moves versions
        Assert.Equal(HttpStatusCode.Forbidden, (await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/settings/group", "{\"mode\":\"test\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/settings/group", ServerRole.Admin, "{\"mode\":\"test\"}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/menu", Menu("two"))).StatusCode);
        var back = await CallAsync(server, HttpMethod.Post, "/api/v1/definitions/app-nav/menu/rollback", ServerRole.Admin, "{}");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(1, (int)JsonNode.Parse(await back.Content.ReadAsStringAsync())!["active"]!);

        // the old per-kind route answers as before, beside the shared one
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Get, "/api/v1/data-sources", ServerRole.Admin)).StatusCode);
    }

    private static string Sign(ConsoleSigningKey key, string title) => DefinitionSigning.Sign(key, "app-nav", "menu", "{\"title\":\"" + title + "\",\"entries\":[]}", []);

    [Fact]
    public async Task An_upload_is_streamed_checked_against_its_hash_and_then_named_by_a_version()
    {
        await StartAsync();
        var server = await PairAsync();
        await PairBuilderAsync();
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/settings/group", ServerRole.Admin, "{\"mode\":\"test\"}")).StatusCode);

        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(server, HttpMethod.Head, sha, ServerRole.Viewer)).StatusCode);

        // the wrong hash keeps nothing; a viewer may not upload
        var wrong = await UploadAsync(server, HttpMethod.Put, new string('0', 64), ServerRole.Publisher, new MemoryStream(bytes));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(server, HttpMethod.Head, new string('0', 64), ServerRole.Viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(server, HttpMethod.Put, sha, ServerRole.Viewer, new MemoryStream(bytes))).StatusCode);

        var stored = await UploadAsync(server, HttpMethod.Put, sha, ServerRole.Publisher, new MemoryStream(bytes));
        Assert.Equal(HttpStatusCode.OK, stored.StatusCode);
        var answer = JsonNode.Parse(await stored.Content.ReadAsStringAsync())!;
        Assert.False((bool)answer["already"]!);
        Assert.Equal(bytes.Length, (long)answer["size"]!);
        Assert.Equal(HttpStatusCode.OK, (await UploadAsync(server, HttpMethod.Head, sha, ServerRole.Viewer)).StatusCode);
        Assert.True((bool)JsonNode.Parse(await (await UploadAsync(server, HttpMethod.Put, sha, ServerRole.Publisher, new MemoryStream(bytes))).Content.ReadAsStringAsync())!["already"]!);

        // a version names the file by its hash; one that names a file the group lacks is refused
        var body = JsonSerializer(new { manifest = "{\"title\":\"t\",\"entries\":[]}", artifacts = new[] { new { path = "a.bin", sha256 = sha, size = bytes.Length } } });
        Assert.Equal(HttpStatusCode.OK, (await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/files", body)).StatusCode);
        var missing = JsonSerializer(new { manifest = "{\"title\":\"u\",\"entries\":[]}", artifacts = new[] { new { path = "b.bin", sha256 = new string('1', 64), size = 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, (await AsPublisherAsync(server, HttpMethod.Put, "/api/v1/definitions/app-nav/files", missing)).StatusCode);
    }
}
