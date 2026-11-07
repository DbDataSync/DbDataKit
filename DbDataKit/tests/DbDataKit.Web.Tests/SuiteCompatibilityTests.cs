using System.Net;
using System.Net.Http.Json;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>
/// Plan 24a UC04: the compatibility generation at pairing and on every call. A server one generation behind pairs; two behind is refused with a message that names both program versions and generations and the
/// side to upgrade; after the server's upgrade the next call succeeds; a registry file without the key loads; the management API integer check of plan 13 is untouched (<see cref="ManagementApiVersionTests"/>).
/// </summary>
public sealed class SuiteCompatibilityTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("generation").FullName;
    private readonly MemorySecrets _secrets = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(3, 4, true)]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 5, false)]
    [InlineData(1, 1, true)]
    [InlineData(1, 2, true)]
    [InlineData(1, 3, false)]
    public void APairIsSupportedWithinOneGenerationEitherWay(int own, int peer, bool supported)
    {
        Assert.Equal(supported, SuiteCompatibility.Supports(peer, own));
        Assert.Equal(supported, SuiteCompatibility.Incompatibility(peer, "1.0", own) is null);
    }

    [Fact]
    public void AServerThatStatesNoGenerationPredatesTheIdeaAndIsTakenToBeInRange() =>
        Assert.Null(SuiteCompatibility.Incompatibility(null, null, own: 5));

    [Fact]
    public void TheMessageNamesBothVersionsBothGenerationsAndTheSideToUpgrade()
    {
        var behind = SuiteCompatibility.Incompatibility(1, "2026.9.1.1000", own: 3, ownProgram: "2026.11.7.1200")!;
        Assert.Contains("2026.9.1.1000", behind);
        Assert.Contains("2026.11.7.1200", behind);
        Assert.Contains("generation 1", behind);
        Assert.Contains("generation 3", behind);
        Assert.Contains("2 generations apart", behind);
        Assert.EndsWith("Upgrade this server.", behind);

        var ahead = SuiteCompatibility.Incompatibility(5, "2027.1.1.0900", own: 3, ownProgram: "2026.11.7.1200")!;
        Assert.EndsWith("Upgrade this console.", ahead);

        // the server's half of the same check, about a client
        var client = SuiteCompatibility.Incompatibility(1, "2026.9.1.1000", own: 3, ownProgram: "2026.11.7.1200", peerRole: "console")!;
        Assert.StartsWith("The console (version 2026.9.1.1000", client);
        Assert.EndsWith("Upgrade this console.", client);
        Assert.EndsWith("Upgrade this server.", SuiteCompatibility.Incompatibility(5, "x", own: 3, peerRole: "console")!);
    }

    // ---- every call ----

    private sealed class Stub(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(answer());
        }
    }

    private static HttpResponseMessage Reply(int? generation, string? program = "2026.9.1.1000")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation(ManagementApi.VersionHeader, ManagementApi.Version.ToString());
        if (generation is { } g) response.Headers.TryAddWithoutValidation(SuiteCompatibility.GenerationHeader, g.ToString());
        if (program is not null) response.Headers.TryAddWithoutValidation(SuiteCompatibility.ProgramVersionHeader, program);
        return response;
    }

    [Fact]
    public async Task EveryCallStatesTheClientsGenerationAndRefusesAServerTwoBehindWithTheMessageAndAnUpgradedServerIsReachedAgain()
    {
        var generation = 1;
        var stub = new Stub(() => Reply(generation));
        var seen = new List<int>();
        using var http = new HttpClient(new ManagementVersionHandler(stub, ownGeneration: 3, observedGeneration: seen.Add));

        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.Equal("3", stub.Seen!.Headers.GetValues(SuiteCompatibility.GenerationHeader).Single());
        Assert.Equal(1, error.ServerGeneration);
        Assert.Contains("2026.9.1.1000", error.Message);
        Assert.EndsWith("Upgrade this server.", error.Message);

        // the server is upgraded to generation 2 (one behind): the next call, the reconnection, succeeds
        generation = 2;
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public async Task AServerOneBehindOrOneAheadAnswersAndOneThatStatesNoGenerationIsServed()
    {
        foreach (var generation in new int?[] { 2, 4, null })
        {
            using var http = new HttpClient(new ManagementVersionHandler(new Stub(() => Reply(generation)), ownGeneration: 3));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken)).StatusCode);
        }
    }

    [Fact]
    public async Task TheManagementApiIntegerIsStillCheckedFirst()
    {
        var response = Reply(1);
        response.Headers.Remove(ManagementApi.VersionHeader);
        response.Headers.TryAddWithoutValidation(ManagementApi.VersionHeader, "9");
        using var http = new HttpClient(new ManagementVersionHandler(new Stub(() => response), ownGeneration: 3));
        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => http.GetAsync("https://server.invalid/x", TestContext.Current.CancellationToken));
        Assert.Contains("management API version 9", error.Message);
        Assert.Null(error.ServerGeneration);
    }

    // ---- pairing ----

    private ConsoleKeyStore Keys() => new(_secrets, _secrets);

    private static Task<TestServer> ServerAt(int generation, string program) =>
        TestServer.StartAsync((app, holder) =>
        {
            app.MapPairing(new PairingService(TestServer.InstanceId, holder.Fingerprint, holder.Clients, holder.Codes), generation, program);
        });

    [Fact]
    public async Task AServerOneGenerationBehindPairsAndTheClientsGenerationIsRecordedOnTheServer()
    {
        await using var server = await ServerAt(generation: 2, program: "2026.10.1.0900");
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);

        var paired = await PairingClient.PairAsync(server.Url, server.Codes.Create(ServerRole.Operator).Code, identity, ct: TestContext.Current.CancellationToken, ownGeneration: 3, ownProgramVersion: "2026.11.7.1200");

        Assert.Equal((2, "2026.10.1.0900"), (paired.Generation, paired.ProgramVersion));
        Assert.NotNull(server.Clients.Find(identity.Id));
        // the file of a single server is not written with the key: the Proxy's strict reader of it would refuse it after a rollback to the release before (a group keeps the generation in its state, see GroupTrustTests)
        Assert.DoesNotContain("generation", File.ReadAllText(Path.Combine(server.Directory, "server.yaml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerTwoGenerationsBehindIsRefusedWithAMessageThatNamesVersionsAndTheSideToUpgradeAndNothingIsPaired()
    {
        await using var server = await ServerAt(generation: 1, program: "2026.9.1.1000");
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);
        var code = server.Codes.Create(ServerRole.Operator).Code;

        var error = await Assert.ThrowsAsync<IncompatibleServerException>(() => PairingClient.PairAsync(server.Url, code, identity, ct: TestContext.Current.CancellationToken, ownGeneration: 3, ownProgramVersion: "2026.11.7.1200"));

        Assert.Equal(1, error.ServerGeneration);
        Assert.Contains("2026.9.1.1000", error.Message);
        Assert.Contains("2026.11.7.1200", error.Message);
        Assert.Contains("generation 1", error.Message);
        Assert.Contains("generation 3", error.Message);
        Assert.EndsWith("Upgrade this server.", error.Message);
        Assert.Empty(server.Clients.All());

        // after the server's upgrade (to generation 2) the same code pairs: it was never spent
        await using var upgraded = await ServerAt(generation: 2, program: "2026.11.1.0900");
        var again = await PairingClient.PairAsync(upgraded.Url, upgraded.Codes.Create(ServerRole.Operator).Code, identity, ct: TestContext.Current.CancellationToken, ownGeneration: 3, ownProgramVersion: "2026.11.7.1200");
        Assert.Equal(2, again.Generation);
    }

    [Fact]
    public async Task AConsoleTwoGenerationsBehindIsRefusedByTheServerBeforeItsCodeIsSpent()
    {
        await using var server = await ServerAt(generation: 5, program: "2027.2.1.0900");
        server.Codes.Create(ServerRole.Operator);
        var identity = await Keys().GetOrCreateAsync(TestContext.Current.CancellationToken);

        // the client's own check passes (it believes it is generation 4 and so one behind); the server sees 3
        using var http = new HttpClient(PairingClient.Pinned(server.Fingerprint));
        var response = await http.PostAsJsonAsync(new Uri(server.Url, ManagementApi.PairingPath),
            new { proof = "x", clientId = "c", name = "c", kind = "console", publicKey = Convert.ToBase64String(identity.Key.PublicKey), generation = 3, programVersion = "2026.8.1.0900" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("2026.8.1.0900", text);
        Assert.Contains("generation 3", text);
        Assert.Contains("generation 5", text);
        Assert.Contains("Upgrade this console.", text);
        Assert.Equal(1, server.Codes.Pending());
    }

    [Fact]
    public async Task APairingFromAnOlderClientThatSendsNoGenerationStillWorksAndTheHelloCarriesTheGeneration()
    {
        await using var server = await ServerAt(generation: 4, program: "2026.11.7.1200");
        using var http = new HttpClient(PairingClient.Pinned(server.Fingerprint));
        var hello = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(new Uri(server.Url, ManagementApi.PairingPath), TestContext.Current.CancellationToken);
        Assert.Equal(4, hello.GetProperty("generation").GetInt32());
        Assert.Equal("2026.11.7.1200", hello.GetProperty("programVersion").GetString());
        Assert.Equal(ManagementApi.Version, hello.GetProperty("apiVersion").GetInt32());
        // and every response states it in a header (the test server's middleware states this build's own)
        Assert.Equal(SuiteCompatibility.Generation.ToString(), (await http.GetAsync(new Uri(server.Url, ManagementApi.PairingPath), TestContext.Current.CancellationToken)).Headers.GetValues(SuiteCompatibility.GenerationHeader).Single());
    }

    // ---- the files ----

    [Fact]
    public void TheRegistryRecordsTheGenerationAndAFileWithoutTheKeyLoads()
    {
        var path = Path.Combine(_directory, "instances.yaml");
        File.WriteAllText(path, "instances:\n  - {id: old-1, name: old, url: 'https://old.example:8443/', fingerprint: abc, apiVersion: 1, paired: '2026-10-01T00:00:00Z'}\n");
        var registry = new InstanceRegistry(path);

        var old = Assert.Single(registry.All());
        Assert.Null(old.Generation);

        registry.Add(new PairedServer(new Uri("https://new.example:8443/"), "new-1", "def", 1, "new", ServerRole.Operator, Generation: 3));
        Assert.Equal(3, registry.Find("new-1")!.Generation);
        Assert.Contains("generation: 3", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(registry.Find("old-1")!.Generation);

        // the server reports another generation after its upgrade; the entry follows, and an unchanged one is not rewritten
        Assert.Equal(4, registry.SetGeneration("new-1", 4)!.Generation);
        var before = File.ReadAllText(path);
        registry.SetGeneration("new-1", 4);
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Null(registry.SetGeneration("nobody", 2));

        // an entry that has a key this reader does not know (a newer console wrote it) still reads
        File.AppendAllText(path, "  - {id: odd-1, name: odd, url: 'https://odd.example:8443/', fingerprint: abc, apiVersion: 1, generation: seven, future: yes, paired: '2026-10-01T00:00:00Z'}\n");
        Assert.Null(registry.Find("odd-1")!.Generation);
    }

    [Fact]
    public void ThePairedClientsFileReadsAGenerationAPersonWroteButDoesNotWriteOne()
    {
        var path = Path.Combine(_directory, "server.yaml");
        var file = new PairedClientsFile(path);
        var key = ConsoleSigningKey.Generate().PublicKey;
        file.Upsert(new PairedClient("c-new", "new", key, ServerRole.Operator, Generation: 2));
        file.Upsert(new PairedClient("c-old", "old", key, ServerRole.Operator));

        Assert.DoesNotContain("generation", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Null(file.Find("c-new")!.Generation);
        Assert.Null(file.Find("c-old")!.Generation);

        File.AppendAllText(path, $"  - {{id: c-hand, name: hand, kind: console, publicKey: {Convert.ToBase64String(key).TrimEnd((char)61).Replace((char)43, (char)45).Replace((char)47, (char)95)}, ceiling: [operator], generation: 4}}\n");
        Assert.Equal(4, file.Find("c-hand")!.Generation);
        Assert.Empty(file.Problems);
    }
}
