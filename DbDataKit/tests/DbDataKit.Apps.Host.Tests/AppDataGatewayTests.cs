using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Apps.Tests;

/// <summary>Plan 26 section 3: an app's data calls go through the apps host, which checks the app's token and forwards to a paired Store with a token of its own that carries the scope, and nothing else.</summary>
public sealed class AppDataGatewayTests : IAsyncLifetime
{
    private const string StoreId = "store-1";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "app-gateway-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity host = new("dbdataproxy-apps", ConsoleSigningKey.Generate());
    private readonly List<(string PathAndQuery, string Method, ValidatedToken Token, string Body)> seen = [];
    private AppInstanceStore instances = null!;
    private WebApplication store = null!;
    private AppDataGateway gateway = null!;
    private AppSession session = null!;
    private Uri storeUrl = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(folder);
        instances = new AppInstanceStore(new DefinitionStore(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DefinitionsModule.Module])));
        instances.PublishApp("schemaVersion: 1\nname: notes\nversion: 1.0.0\nentry: index.html\nroles: [viewer]\nmodels:\n  - model: territories\n    entities: {territory: [read, write], region: [read]}\n", "ada");
        instances.Save(new AppInstance("acme", "notes", Bindings: new Dictionary<string, string> { ["territories"] = "acme-data" }), "ada");

        // the store: a pinned self-signed certificate, and the validation a Store does (the host is paired as a server client)
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        var port = Free();
        var validator = new ConsoleTokenValidator(StoreId, new Paired(new PairedClient(host.Id, "apps host", host.Key.PublicKey, ServerRole.Viewer | ServerRole.Author, ClientKind.Server)), new MemoryReplayCache());
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, l => l.UseHttps(certificate)));
        store = builder.Build();
        store.Map("/{**path}", async (HttpContext context) =>
        {
            context.Response.Headers[ManagementApi.VersionHeader] = ManagementApi.Version.ToString();
            var result = validator.Validate(context.Request.Headers[ManagementAuthentication.TokenHeader]);
            if (result.Token is not { } token) { context.Response.StatusCode = 401; return; }
            using var reader = new StreamReader(context.Request.Body);
            seen.Add((context.Request.Path + context.Request.QueryString.Value, context.Request.Method, token, await reader.ReadToEndAsync()));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"ok\":true}");
        });
        await store.StartAsync();
        storeUrl = new Uri($"https://localhost:{port}/");

        var stores = new AppStores(Path.Combine(folder, "stores.json"));
        stores.Save(new AppStore(StoreId, storeUrl, PairingProof.CertificateFingerprint(certificate), "Store"));
        gateway = new AppDataGateway(instances, stores, host);
        session = new AppSessions().Create(new DbDataKit.Identity.HomeIdentity("user-7", "Ada", null, [], new Dictionary<string, IReadOnlyList<string>> { ["app:acme"] = ["author"] }));
    }

    public async ValueTask DisposeAsync()
    {
        await store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(folder, true); } catch (IOException) { }
    }

    private static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Paired(PairedClient client) : IPairedClients
    {
        public PairedClient? Find(string clientId) => clientId == client.Id ? client : null;
    }

    private string AppToken() => new AppTokenBroker(host.Issuer()).Issue(session, "acme").Token;

    private Task<AppResponse> Call(string path, string? token, string method = "GET", string query = "", string body = "") =>
        gateway.ForwardAsync("acme", StoreId, path, method, query, token is null ? null : "Bearer " + token, "application/json", Encoding.UTF8.GetBytes(body), default);

    [Fact]
    public async Task A_call_with_the_apps_token_reaches_the_store_with_a_token_of_the_hosts_that_carries_the_person_the_app_and_the_scope()
    {
        var response = await Call("api/v1/models/territories/entities/territory/rows", AppToken(), query: "?limit=5");

        Assert.Equal(200, response.Status);
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(response.Body));
        var call = Assert.Single(seen);
        Assert.Equal("/api/v1/models/territories/instances/acme-data/entities/territory/rows?limit=5", call.PathAndQuery);   // the instance's binding chose the data
        Assert.Equal(("user-7", "acme"), (call.Token.SubjectId, call.Token.App));
        Assert.Equal(["territories/territory:write", "territories/region:read"], call.Token.Scope);
        Assert.Equal(ServerRole.Author, call.Token.Roles & ServerRole.Author);
        Assert.Equal("*", response.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task A_post_is_forwarded_with_its_body_and_an_explicit_instance_in_the_path_is_left_alone()
    {
        var response = await Call("api/v1/models/territories/instances/other/changes", AppToken(), "POST", body: "{\"changes\":[]}");

        Assert.Equal(200, response.Status);
        var call = Assert.Single(seen);
        Assert.Equal(("/api/v1/models/territories/instances/other/changes", "POST", "{\"changes\":[]}"), (call.PathAndQuery, call.Method, call.Body));
    }

    [Theory]
    [InlineData("api/v1/models")]
    [InlineData("api/v1/models/territories/storage/create")]
    [InlineData("api/v1/models/territories/requests/1/approve")]
    [InlineData("api/v1/info")]
    [InlineData("api/v1/models/territories/entities/territory/../../storage")]
    public async Task Only_row_reads_and_changes_are_forwarded(string path)
    {
        Assert.Equal(403, (await Call(path, AppToken())).Status);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_missing_foreign_or_expired_token_is_refused_before_the_store_is_asked()
    {
        const string Path = "api/v1/models/territories/entities/territory/rows";
        Assert.Equal(401, (await Call(Path, null)).Status);
        Assert.Equal(401, (await Call(Path, "not-a-token")).Status);

        // a token signed by some other key, and a token for another instance's audience
        var stranger = new ConsoleIdentity(host.Id, ConsoleSigningKey.Generate());
        Assert.Equal(401, (await Call(Path, new AppTokenBroker(stranger.Issuer()).Issue(session, "acme").Token)).Status);
        var other = new AppTokenBroker(host.Issuer()).Issue(new AppSessions().Create(session.Identity with { Roles = new Dictionary<string, IReadOnlyList<string>> { ["app:globex"] = ["author"] } }), "globex").Token;
        Assert.Equal(401, (await Call(Path, other)).Status);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task An_unpaired_store_and_an_unknown_instance_are_404()
    {
        Assert.Equal(404, (await gateway.ForwardAsync("acme", "store-9", "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + AppToken(), null, [], default)).Status);
        Assert.Equal(404, (await gateway.ForwardAsync("globex", StoreId, "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + AppToken(), null, [], default)).Status);
    }

    [Fact]
    public async Task A_store_that_cannot_be_reached_is_a_502_and_one_with_another_certificate_is_not_trusted()
    {
        var stores = new AppStores(Path.Combine(folder, "other.json"));
        stores.Save(new AppStore(StoreId, storeUrl, "0000000000000000000000000000000000000000000000000000000000000000", "Store"));
        var pinned = new AppDataGateway(instances, stores, host);
        Assert.Equal(502, (await pinned.ForwardAsync("acme", StoreId, "api/v1/models/territories/entities/territory/rows", "GET", "", "Bearer " + AppToken(), null, [], default)).Status);
        Assert.Empty(seen);
    }
}
