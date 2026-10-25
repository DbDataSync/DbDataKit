using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>A real Kestrel on loopback with a self-signed certificate: the pairing exchange as a console and a server would have it.</summary>
internal sealed class TestServer : IAsyncDisposable
{
    public const string InstanceId = "server-instance-1";
    private readonly WebApplication _app;

    public Uri Url { get; }
    public string Id { get; }
    public string Fingerprint { get; }
    public string Directory { get; }
    public PairedClientsFile Clients { get; }
    public PairingCodes Codes { get; }

    private TestServer(WebApplication app, Uri url, string fingerprint, string directory, string id)
    {
        (_app, Url, Fingerprint, Directory, Id) = (app, url, fingerprint, directory, id);
        Clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        Codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));
    }

    public static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null); // Windows wants a key it can hand to Schannel
    }

    public static async Task<TestServer> StartAsync(Action<WebApplication, TestServer>? map = null, X509Certificate2? certificate = null, string instanceId = InstanceId)
    {
        var directory = System.IO.Directory.CreateTempSubdirectory("pairing-e2e").FullName;
        certificate ??= SelfSigned();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);
        var holder = new TestServer(app, new Uri("https://127.0.0.1:1"), fingerprint, directory, instanceId);
        if (map is null)
        {
            app.MapPairing(new PairingService(instanceId, fingerprint, holder.Clients, holder.Codes));
            app.MapKeyRotation(new KeyRotationService(instanceId, holder.Clients, new MemoryReplayCache()));
        }
        else map(app, holder);
        await app.StartAsync();
        var url = new Uri(app.Urls.First());
        return new TestServer(app, url, fingerprint, directory, instanceId);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        System.IO.Directory.Delete(Directory, recursive: true);
    }
}

internal sealed class MemorySecrets : ISecretStore, ISecretProvider
{
    public Dictionary<string, string> Values { get; } = [];
    public string Name => "memory";
    public bool CanHandle(SecretRef reference) => true;
    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default) { Values[name.ToString()] = value.RevealString(); return Task.CompletedTask; }
    public Task DeleteAsync(SecretName name, CancellationToken ct = default) { Values.Remove(name.ToString()); return Task.CompletedTask; }
    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var key in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))) yield return SecretName.Parse(key);
        await Task.CompletedTask;
    }
    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default) =>
        ValueTask.FromResult(Values.TryGetValue(reference.Target, out var v) ? SecretValue.From(v) : null);
}

public sealed class PairingEndToEndTests : IAsyncLifetime
{
    private readonly string _consoleDir = Directory.CreateTempSubdirectory("console").FullName;
    private TestServer _server = null!;
    private readonly MemorySecrets _secrets = new();

    public async ValueTask InitializeAsync() => _server = await TestServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_consoleDir, recursive: true);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private ConsoleKeyStore Keys() => new(_secrets, _secrets);
    private InstanceRegistry Registry() => new(Path.Combine(_consoleDir, "instances.yaml"));

    // ---- the exchange ----

    [Fact]
    public async Task AConsolePairsWithAServerOverHttpsWithASelfSignedCertificate()
    {
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var code = _server.Codes.Create(ServerRole.Operator, "ops").Code;

        var paired = await PairingClient.PairAsync(_server.Url, code, identity, ct: TestContext.Current.CancellationToken);

        Assert.Equal((TestServer.InstanceId, _server.Fingerprint, ManagementApi.Version, "ops"), (paired.InstanceId, paired.CertificateFingerprint, paired.ApiVersion, paired.Name));
        Assert.True(ServerRoles.Allows(paired.Ceiling, ServerRole.Operator));
        Assert.False(ServerRoles.Allows(paired.Ceiling, ServerRole.Admin));

        var onServer = _server.Clients.Find(identity.Id)!;
        Assert.Equal(identity.Key.PublicKey, onServer.PublicKey);
        Assert.Equal(ClientKind.Console, onServer.Kind);
    }

    [Fact]
    public async Task AfterPairingTheConsolesTokensAreAcceptedByTheServerUpToTheCeiling()
    {
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var paired = await PairingClient.PairAsync(_server.Url, _server.Codes.Create(ServerRole.Operator).Code, identity, ct: TestContext.Current.CancellationToken);
        var validator = new ConsoleTokenValidator(TestServer.InstanceId, _server.Clients, new MemoryReplayCache());
        var issuer = identity.Issuer();

        Assert.True(validator.Validate(issuer.Issue(new ConsoleTokenRequest(paired.InstanceId, "user-1", "Ada", ServerRole.Operator))).Ok);
        Assert.Equal(TokenFailure.AboveCeiling, validator.Validate(issuer.Issue(new ConsoleTokenRequest(paired.InstanceId, "user-1", "Ada", ServerRole.Admin))).Failure);
    }

    [Fact]
    public async Task AServerClientPairsAsAServer()
    {
        var self = new ConsoleIdentity("build-prod", ConsoleSigningKey.Generate());
        await PairingClient.PairAsync(_server.Url, _server.Codes.Create(ServerRole.Viewer).Code, self, ClientKind.Server, "build", TestContext.Current.CancellationToken);
        Assert.Equal(ClientKind.Server, _server.Clients.Find("build-prod")!.Kind);
    }

    [Fact]
    public async Task APairedServerIsReachedOnlyThroughItsPinnedCertificate()
    {
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var paired = await PairingClient.PairAsync(_server.Url, _server.Codes.Create(ServerRole.Viewer).Code, identity, ct: TestContext.Current.CancellationToken);

        using var pinned = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint));
        Assert.Equal(HttpStatusCode.OK, (await pinned.GetAsync(new Uri(_server.Url, ManagementApi.PairingPath), TestContext.Current.CancellationToken)).StatusCode);

        await using var other = await TestServer.StartAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => pinned.GetAsync(new Uri(other.Url, ManagementApi.PairingPath), TestContext.Current.CancellationToken));
    }

    // ---- refusals ----

    [Fact]
    public async Task AWrongCodeIsRefusedAndNothingIsPaired()
    {
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        _server.Codes.Create(ServerRole.Admin);
        var error = await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(_server.Url, "AAAAA-AAAAA-AAAAA-AAAAA", identity, ct: TestContext.Current.CancellationToken));
        Assert.Contains("did not accept the code", error.Message);
        Assert.Empty(_server.Clients.All());
    }

    [Fact]
    public async Task ACodeThatHasBeenUsedIsRefused()
    {
        var code = _server.Codes.Create(ServerRole.Viewer).Code;
        await PairingClient.PairAsync(_server.Url, code, new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(_server.Url, code, new ConsoleIdentity("two", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
        Assert.Single(_server.Clients.All());
    }

    [Fact]
    public async Task AnExpiredCodeIsRefused()
    {
        var code = _server.Codes.Create(ServerRole.Viewer, lifetime: TimeSpan.FromMilliseconds(1)).Code;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(_server.Url, code, new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APlainHttpAddressAndAMalformedCodeAreRefusedBeforeAnythingIsSent()
    {
        var identity = new ConsoleIdentity("one", ConsoleSigningKey.Generate());
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(new Uri("http://127.0.0.1:1"), "AAAAA-AAAAA-AAAAA-AAAAA", identity, ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(_server.Url, "short", identity, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASilentAddressIsReportedNotHung()
    {
        // a closed port refuses at once on Linux and may be dropped silently on Windows: the caller's token ends the wait either way
        var identity = new ConsoleIdentity("one", ConsoleSigningKey.Generate());
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        patience.CancelAfter(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(new Uri("https://127.0.0.1:1"), "AAAAA-AAAAA-AAAAA-AAAAA", identity, ct: patience.Token));
        Assert.Contains("did not answer", error.Message);
    }

    [Fact]
    public async Task TheCodeIsNotInTheRequest()
    {
        var code = _server.Codes.Create(ServerRole.Viewer).Code;
        var key = PairingCodes.KeyFor(code)!;
        var identity = new ConsoleIdentity("one", ConsoleSigningKey.Generate());
        var proof = PairingProof.ForRequest(key, identity.Id, identity.Key.PublicKey, ClientKind.Console, _server.Fingerprint);
        Assert.DoesNotContain(code.Replace("-", ""), proof);
        Assert.DoesNotContain(B64(key), proof);
        // and the proof is for one certificate, one identity and one kind only
        Assert.NotEqual(proof, PairingProof.ForRequest(key, identity.Id, identity.Key.PublicKey, ClientKind.Console, new string('0', 64)));
        Assert.NotEqual(proof, PairingProof.ForRequest(key, "two", identity.Key.PublicKey, ClientKind.Console, _server.Fingerprint));
        Assert.NotEqual(proof, PairingProof.ForRequest(key, identity.Id, identity.Key.PublicKey, ClientKind.Server, _server.Fingerprint));
    }

    // ---- someone in the middle ----

    private static Task<TestServer> Impostor(Func<TestServer, object> answer) =>
        TestServer.StartAsync((app, self) =>
        {
            app.MapGet(ManagementApi.PairingPath, () => Results.Json(new { instanceId = TestServer.InstanceId, apiVersion = 1 }));
            app.MapPost(ManagementApi.PairingPath, () => Results.Json(answer(self)));
        });

    [Fact]
    public async Task AnImpostorWithItsOwnCertificateCannotMakeTheServersProof()
    {
        // it answers as the real server would, claiming the real server's fingerprint and a made-up proof
        await using var impostor = await Impostor(_ => new { instanceId = TestServer.InstanceId, certificateFingerprint = _server.Fingerprint, serverProof = "AAAA", apiVersion = 1, name = "x", ceiling = new[] { "admin" } });
        var code = _server.Codes.Create(ServerRole.Admin).Code;
        var error = await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(impostor.Url, code, new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
        Assert.Contains("did not prove", error.Message);
    }

    [Fact]
    public async Task AnImpostorThatClaimsItsOwnFingerprintStillCannotMakeTheProof()
    {
        await using var impostor = await Impostor(self => new { instanceId = TestServer.InstanceId, certificateFingerprint = self.Fingerprint, serverProof = "AAAA", apiVersion = 1, name = "x", ceiling = new[] { "admin" } });
        var code = _server.Codes.Create(ServerRole.Admin).Code;
        await Assert.ThrowsAsync<PairingFailedException>(() => PairingClient.PairAsync(impostor.Url, code, new ConsoleIdentity("one", ConsoleSigningKey.Generate()), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARelayInTheMiddleCannotReuseTheClientsProofAgainstTheRealServer()
    {
        // the relay terminates TLS with its own certificate, so the proof it receives names the relay's fingerprint; the real server (a different certificate) refuses it
        var code = _server.Codes.Create(ServerRole.Admin).Code;
        var key = PairingCodes.KeyFor(code)!;
        var identity = new ConsoleIdentity("one", ConsoleSigningKey.Generate());
        var proofForRelay = PairingProof.ForRequest(key, identity.Id, identity.Key.PublicKey, ClientKind.Console, new string('a', 64));

        using var http = new HttpClient(PairingClient.Pinned(_server.Fingerprint));
        var response = await http.PostAsJsonAsync(new Uri(_server.Url, ManagementApi.PairingPath),
            new { proof = proofForRelay, clientId = identity.Id, kind = "console", publicKey = B64(identity.Key.PublicKey) }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_server.Clients.All());
    }

    // ---- the endpoint ----

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"robot\"}")]
    [InlineData("[1,2,3]")]
    public async Task TheEndpointAnswers400ForARequestItCannotRead(string body)
    {
        using var http = new HttpClient(PairingClient.Pinned(_server.Fingerprint));
        var response = await http.PostAsync(new Uri(_server.Url, ManagementApi.PairingPath), new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheEndpointRefusesABodyBeyondItsLimit()
    {
        using var http = new HttpClient(PairingClient.Pinned(_server.Fingerprint));
        var big = "{\"proof\":\"" + new string('a', 100_000) + "\",\"kind\":\"console\"}";
        var response = await http.PostAsync(new Uri(_server.Url, ManagementApi.PairingPath), new StringContent(big, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task TheRefusalDoesNotSayWhyTheCodeFailed()
    {
        using var http = new HttpClient(PairingClient.Pinned(_server.Fingerprint));
        var key = ConsoleSigningKey.Generate();
        var response = await http.PostAsJsonAsync(new Uri(_server.Url, ManagementApi.PairingPath),
            new { proof = "x", clientId = "one", kind = "console", publicKey = B64(key.PublicKey) }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"the pairing was not accepted\"}", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    // ---- the console's key and instances ----

    [Fact]
    public async Task TheConsoleKeepsOneIdentityInTheSecretStore()
    {
        var first = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var second = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal((first.Id, Convert.ToHexString(first.Key.PublicKey)), (second.Id, Convert.ToHexString(second.Key.PublicKey)));
        Assert.StartsWith("console-", first.Id);
        Assert.Single(_secrets.Values);
    }

    [Fact]
    public async Task ADamagedKeyInTheStoreIsReportedNotReplaced()
    {
        _secrets.Values[ConsoleKeyStore.Name.ToString()] = "console-x:not-a-seed";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken));
        Assert.Contains("damaged", error.Message);
    }

    [Fact]
    public async Task InstancesAreRememberedByTheRegistryAndFoundByNameOrId()
    {
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var paired = await PairingClient.PairAsync(_server.Url, _server.Codes.Create(ServerRole.Viewer).Code, identity, ct: TestContext.Current.CancellationToken);
        Registry().Add(paired, "East sync");

        var again = Registry(); // a new process reads the same file
        var found = again.Find("east SYNC")!;
        Assert.Equal((TestServer.InstanceId, _server.Fingerprint, _server.Url), (found.InstanceId, found.CertificateFingerprint, found.Url));
        Assert.Null(again.Find("nobody"));
        Assert.Equal(found.InstanceId, again.Find(TestServer.InstanceId)!.InstanceId);
        Assert.True(again.Remove(TestServer.InstanceId));
        Assert.Empty(again.All());
    }

    [Fact]
    public void PairingTheSameServerAgainReplacesItsEntryAndAnAmbiguousNameFindsNothing()
    {
        var registry = Registry();
        PairedServer Server(string id, string pin) => new(new Uri("https://h:1/"), id, pin, 1, id, ServerRole.Viewer);
        registry.Add(Server("a", "pin1"), "Same");
        registry.Add(Server("a", "pin2"), "Same");
        Assert.Equal("pin2", Assert.Single(registry.All()).CertificateFingerprint);
        registry.Add(Server("b", "pin3"), "Same");
        Assert.Null(registry.Find("Same"));
        Assert.NotNull(registry.Find("b"));
    }

    [Fact]
    public void AnInstanceWithoutAPinOrOverPlainHttpIsNotTrusted()
    {
        File.WriteAllText(Path.Combine(_consoleDir, "instances.yaml"),
            "schemaVersion: 1\ninstances:\n  - {id: nopin, name: x, url: \"https://h:1/\"}\n  - {id: plain, name: x, url: \"http://h:1/\", fingerprint: ab}\n  - {id: ok, name: x, url: \"https://h:1/\", fingerprint: ab}\n");
        Assert.Equal(["ok"], Registry().All().Select(i => i.InstanceId));
    }
}
