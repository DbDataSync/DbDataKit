using System.Net;
using System.Net.Http.Json;
using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

public sealed class KeyRotationTests : IDisposable
{
    private const string Instance = "server-instance-1";
    private readonly string _dir = Directory.CreateTempSubdirectory("rotation").FullName;
    private readonly TestClock _clock = new();
    private readonly ConsoleSigningKey _old = ConsoleSigningKey.Generate();
    private readonly ConsoleSigningKey _new = ConsoleSigningKey.Generate();
    private readonly PairedClientsFile _clients;
    private readonly MemoryReplayCache _replay;
    private readonly KeyRotationService _service;

    public KeyRotationTests()
    {
        _clients = new PairedClientsFile(Path.Combine(_dir, "server.yaml"), _clock);
        _replay = new MemoryReplayCache(_clock);
        _service = new KeyRotationService(Instance, _clients, _replay, _clock);
        _clients.Upsert(new PairedClient("console-a", "Operations", _old.PublicKey, ServerRole.Operator, ClientKind.Cli));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private KeyRotationRequest Request(ConsoleSigningKey? current = null, ConsoleSigningKey? next = null, string instance = Instance, string client = "console-a") =>
        KeyRotationRequest.Create(instance, client, current ?? _old, next ?? _new, _clock.Now);

    private string Token(ConsoleSigningKey key) => new ConsoleTokenIssuer("console-a", key, _clock).Issue(new ConsoleTokenRequest(Instance, "user-1", "Ada", ServerRole.Viewer));

    private ConsoleTokenValidator Validator() => new(Instance, _clients, new MemoryReplayCache(_clock), _clock);

    // ---- the server ----

    [Fact]
    public void TheKeyIsReplacedAndNothingElseAboutThePairingChanges()
    {
        var before = _clients.Find("console-a")!;
        Assert.True(_service.Rotate(Request()).Ok);
        var after = _clients.Find("console-a")!;
        Assert.Equal(_new.PublicKey, after.PublicKey);
        Assert.Equal((before.Name, before.Kind, before.Ceiling, before.PairedAt), (after.Name, after.Kind, after.Ceiling, after.PairedAt));
    }

    [Fact]
    public void AfterRotationOldTokensFailAndNewOnesPassWithoutPairingAgain()
    {
        var validator = Validator();
        Assert.True(validator.Validate(Token(_old)).Ok);
        Assert.True(_service.Rotate(Request()).Ok);
        Assert.Equal(TokenFailure.BadSignature, validator.Validate(Token(_old)).Failure);
        Assert.True(validator.Validate(Token(_new)).Ok);
    }

    [Fact]
    public void ARequestNotSignedByTheKeyOnRecordIsRefused()
    {
        var stranger = ConsoleSigningKey.Generate();
        var result = _service.Rotate(Request(current: stranger));
        Assert.Equal(RotationFailure.BadOldSignature, result.Failure);
        Assert.Equal(_old.PublicKey, _clients.Find("console-a")!.PublicKey);
    }

    [Fact]
    public void AKeyTheRequesterDoesNotHoldCannotBeRegistered()
    {
        var request = Request() with { SignedByNew = _old.Sign(KeyRotationRequest.Message(Instance, "console-a", _new.PublicKey, _clock.Now.ToUnixTimeSeconds(), "x")) };
        Assert.Equal(RotationFailure.BadNewSignature, _service.Rotate(request).Failure);
        var swapped = Request();
        Assert.Equal(RotationFailure.BadOldSignature, _service.Rotate(swapped with { NewPublicKey = ConsoleSigningKey.Generate().PublicKey }).Failure); // the signature covers the key
    }

    [Fact]
    public void ARequestForAnotherServerIsRefusedHere()
    {
        Assert.Equal(RotationFailure.BadOldSignature, _service.Rotate(Request(instance: "some-other-server")).Failure);
    }

    [Fact]
    public void ARequestForAClientThatIsNotPairedIsRefused()
    {
        Assert.Equal(RotationFailure.UnknownClient, _service.Rotate(Request(client: "nobody")).Failure);
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(-59, true)]
    [InlineData(61, false)]
    [InlineData(-61, false)]
    public void ARequestIsGoodForAMinuteEitherSideOfNow(int offsetSeconds, bool accepted)
    {
        var request = Request();
        _clock.Advance(TimeSpan.FromSeconds(offsetSeconds));
        Assert.Equal(accepted, _service.Rotate(request).Ok);
        if (!accepted) Assert.Equal(RotationFailure.Stale, _service.Rotate(request).Failure);
    }

    [Fact]
    public void ACapturedRequestCannotBeReplayedAfterTheKeyHasMovedOn()
    {
        var captured = Request();
        Assert.True(_service.Rotate(captured).Ok);
        var third = ConsoleSigningKey.Generate();
        Assert.True(_service.Rotate(Request(current: _new, next: third)).Ok);
        Assert.False(_service.Rotate(captured).Ok);
        Assert.Equal(third.PublicKey, _clients.Find("console-a")!.PublicKey);
    }

    [Fact]
    public void TheOldKeyCannotRotateAgainOnceItHasBeenReplaced()
    {
        Assert.True(_service.Rotate(Request()).Ok);
        var attacker = ConsoleSigningKey.Generate();
        Assert.Equal(RotationFailure.BadOldSignature, _service.Rotate(Request(current: _old, next: attacker)).Failure);
        Assert.Equal(_new.PublicKey, _clients.Find("console-a")!.PublicKey);
    }

    [Fact]
    public void AskingAgainForTheKeyAlreadyOnRecordSucceedsAndChangesNothing()
    {
        Assert.True(_service.Rotate(Request()).Ok);
        var again = _service.Rotate(Request()); // a fresh request from a console that did not hear the answer: signed by the old key, which is no longer on record
        Assert.True(again.Ok);
        Assert.Equal("already on record", again.Detail);
        Assert.Equal(_new.PublicKey, _clients.Find("console-a")!.PublicKey);
        // but not by someone who only holds the old key and names a key it does not hold
        Assert.False(_service.Rotate(Request(current: _old, next: _old)).Ok);
    }

    [Fact]
    public void AMalformedRequestIsRefused()
    {
        Assert.Equal(RotationFailure.BadShape, _service.Rotate(Request() with { NewPublicKey = new byte[5] }).Failure);
        Assert.Equal(RotationFailure.BadShape, _service.Rotate(Request() with { Nonce = "" }).Failure);
        Assert.Equal(RotationFailure.BadShape, _service.Rotate(Request() with { Nonce = new string('n', 65) }).Failure);
        Assert.Equal(RotationFailure.BadShape, _service.Rotate(Request() with { ClientId = "" }).Failure);
        Assert.Equal(RotationFailure.BadOldSignature, _service.Rotate(Request() with { SignedByOld = [] }).Failure);
    }

    // ---- the console's key store ----

    private readonly MemorySecrets _secrets = new();
    private ConsoleKeyStore Keys() => new(_secrets, _secrets);

    [Fact]
    public async Task ARotationKeepsTheIdAndTheCurrentKeyUntilItIsPromoted()
    {
        var current = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var next = await Keys().BeginRotationAsync(TestContext.Current.CancellationToken);
        Assert.Equal(current.Id, next.Id);
        Assert.NotEqual(current.Key.Fingerprint, next.Key.Fingerprint);
        Assert.Equal(next.Key.Fingerprint, (await Keys().BeginRotationAsync(TestContext.Current.CancellationToken)).Key.Fingerprint); // continuing, not starting another
        Assert.Equal(current.Key.Fingerprint, (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Key.Fingerprint);
        Assert.Equal(current.Key.Fingerprint, (await Keys().ForKeyAsync(current.Key.Fingerprint, TestContext.Current.CancellationToken))!.Key.Fingerprint);
        Assert.Equal(next.Key.Fingerprint, (await Keys().ForKeyAsync(next.Key.Fingerprint, TestContext.Current.CancellationToken))!.Key.Fingerprint);
        Assert.Null(await Keys().ForKeyAsync("0000000000000000", TestContext.Current.CancellationToken));

        await Keys().PromoteNextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(next.Key.Fingerprint, (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Key.Fingerprint);
        Assert.Null(await Keys().NextAsync(TestContext.Current.CancellationToken));
        Assert.Null(await Keys().ForKeyAsync(current.Key.Fingerprint, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Keys().PromoteNextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheRegistryRemembersWhichKeyEachServerHas()
    {
        var registry = new InstanceRegistry(Path.Combine(_dir, "instances.yaml"));
        registry.Add(new PairedServer(new Uri("https://h:1/"), "a", "pin", 1, "a", ServerRole.Viewer), keyFingerprint: "aaaa");
        Assert.Equal("aaaa", new InstanceRegistry(Path.Combine(_dir, "instances.yaml")).Find("a")!.KeyFingerprint);
        registry.SetKey("a", "bbbb");
        Assert.Equal("bbbb", new InstanceRegistry(Path.Combine(_dir, "instances.yaml")).Find("a")!.KeyFingerprint);
        Assert.Throws<InvalidOperationException>(() => registry.SetKey("nobody", "cccc"));
    }
}

/// <summary>The whole rotation as a console runs it: real servers, the secret store and the registry.</summary>
public sealed class ConsoleKeyRotationEndToEndTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rotation-e2e").FullName;
    private readonly MemorySecrets _secrets = new();
    private readonly List<TestServer> _servers = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private ConsoleKeyStore Keys() => new(_secrets, _secrets);
    private InstanceRegistry Registry() => new(Path.Combine(_dir, "instances.yaml"));

    private async Task<TestServer> PairedServerAsync(InstanceRegistry registry, string name)
    {
        var server = await TestServer.StartAsync(instanceId: name);
        _servers.Add(server);
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var paired = await PairingClient.PairAsync(server.Url, server.Codes.Create(ServerRole.Operator).Code, identity, ct: TestContext.Current.CancellationToken);
        registry.Add(paired, name, identity.Key.Fingerprint);
        return server;
    }

    private static string TokenFrom(TestServer server, ConsoleIdentity who) => who.Issuer().Issue(new ConsoleTokenRequest(server.Id, "user-1", "Ada", ServerRole.Viewer));

    private static bool Accepts(TestServer server, ConsoleIdentity who) => new ConsoleTokenValidator(server.Id, server.Clients, new MemoryReplayCache()).Validate(TokenFrom(server, who)).Ok;

    [Fact]
    public async Task EveryServerTakesTheNewKeyAndOnlyThenDoesItBecomeTheCurrentOne()
    {
        var registry = Registry();
        var a = await PairedServerAsync(registry, "a");
        var b = await PairedServerAsync(registry, "b");
        var old = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);

        var reports = await ConsoleKeyRotation.RunAsync(Keys(), registry, ct: TestContext.Current.CancellationToken);

        Assert.All(reports, r => Assert.True(r.Rotated, r.Problem));
        var current = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(old.Key.Fingerprint, current.Key.Fingerprint);
        Assert.Equal(old.Id, current.Id);
        Assert.Null(await Keys().NextAsync(TestContext.Current.CancellationToken));
        Assert.True(Accepts(a, current) && Accepts(b, current));
        Assert.False(Accepts(a, old) || Accepts(b, old));
        Assert.All(registry.All(), i => Assert.Equal(current.Key.Fingerprint, i.KeyFingerprint));
    }

    [Fact]
    public async Task AServerThatCannotBeReachedKeepsTheOldKeyWorkingAndTheRotationContinuesLater()
    {
        var registry = Registry();
        var a = await PairedServerAsync(registry, "a");
        var b = await PairedServerAsync(registry, "b");
        var old = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        await b.DisposeAsync();
        _servers.Remove(b);

        var reports = await ConsoleKeyRotation.RunAsync(Keys(), registry, ct: TestContext.Current.CancellationToken);

        Assert.True(reports.Single(r => r.Instance.Name == "a").Rotated);
        Assert.False(reports.Single(r => r.Instance.Name == "b").Rotated);
        Assert.Equal(old.Key.Fingerprint, (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Key.Fingerprint); // not promoted yet
        var next = (await Keys().NextAsync(TestContext.Current.CancellationToken))!;
        Assert.True(Accepts(a, next));
        // the console signs for each server with the key that server has
        var forA = await Keys().ForKeyAsync(registry.Find("a")!.KeyFingerprint!, TestContext.Current.CancellationToken);
        var forB = await Keys().ForKeyAsync(registry.Find("b")!.KeyFingerprint!, TestContext.Current.CancellationToken);
        Assert.Equal((next.Key.Fingerprint, old.Key.Fingerprint), (forA!.Key.Fingerprint, forB!.Key.Fingerprint));

        // running it again does not send a to the server twice, and still cannot finish while b is gone
        Assert.True((await ConsoleKeyRotation.RunAsync(Keys(), registry, ct: TestContext.Current.CancellationToken)).Single(r => r.Instance.Name == "a").Rotated);
        Assert.NotNull(await Keys().NextAsync(TestContext.Current.CancellationToken));

        // once b is taken off the list the rotation can finish
        registry.Remove("b");
        Assert.All(await ConsoleKeyRotation.RunAsync(Keys(), registry, ct: TestContext.Current.CancellationToken), r => Assert.True(r.Rotated));
        Assert.Equal(next.Key.Fingerprint, (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Key.Fingerprint);
    }

    [Fact]
    public async Task AServerThatHasUnpairedTheConsoleSaysSoAndKeepsTheRotationOpen()
    {
        var registry = Registry();
        var a = await PairedServerAsync(registry, "a");
        var id = (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Id;
        a.Clients.Remove(id);

        var report = Assert.Single(await ConsoleKeyRotation.RunAsync(Keys(), registry, ct: TestContext.Current.CancellationToken));
        Assert.False(report.Rotated);
        Assert.Contains("did not accept the new key", report.Problem);
        Assert.NotNull(await Keys().NextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARotationWithNoServersJustMovesToTheNewKey()
    {
        var old = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await ConsoleKeyRotation.RunAsync(Keys(), Registry(), ct: TestContext.Current.CancellationToken));
        Assert.NotEqual(old.Key.Fingerprint, (await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken)).Key.Fingerprint);
    }

    [Fact]
    public async Task TheEndpointRefusesGarbageAndDoesNotSayWhy()
    {
        var registry = Registry();
        var a = await PairedServerAsync(registry, "a");
        using var http = new HttpClient(PairingClient.Pinned(a.Fingerprint));
        var garbage = await http.PostAsync(new Uri(a.Url, ManagementApi.RotationPath), new StringContent("nonsense", System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        var refused = await http.PostAsJsonAsync(new Uri(a.Url, ManagementApi.RotationPath),
            new { clientId = "x", newPublicKey = "AAAA", timestamp = 1, nonce = "n", signedByOld = "AAAA", signedByNew = "AAAA" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("{\"error\":\"the key rotation was not accepted\"}", await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
