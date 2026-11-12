using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 30 PB03: the runner uses the group's trust store through its <c>require_signatures</c> switch, and its instances verify what they prepare.</summary>
public sealed class PublishTrustTests
{
    public static TheoryData<string> Engines => StateEngines.Engines;

    private static string Text(JsonNode? node, string key) => (string)node![key]!;

    private static JsonObject Signed(FakePackage package, ConsoleSigningKey key, string? manifestSigned = null)
    {
        var body = JsonSerializer.SerializeToNode(package.Body("dev", "stage"))!.AsObject();
        var files = package.Files.Select(f => new ArtifactRef(f.Key, FakePackage.Sha(f.Value), f.Value.Length, null));
        body["signature"] = DefinitionSigning.Sign(key, "integration", package.Name, manifestSigned ?? package.Manifest(), files);
        return body;
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task With_require_signatures_a_version_signed_by_a_trusted_key_is_accepted_and_an_unsigned_a_forged_or_a_revoked_one_is_refused(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(publisher);
        var key = ConsoleSigningKey.Generate();
        var keyBody = new { public_key = Url.Encode(key.PublicKey), label = "ci" };

        // only an administrator manages the keys; a publisher cannot trust its own
        Assert.Equal(HttpStatusCode.Forbidden, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/trust/integration/keys", keyBody)).Status);
        var (added, addedBody) = await h.CallAsync(HttpMethod.Put, "/api/v1/trust/integration/keys", ServerRole.Admin, keyBody);
        Assert.Equal(HttpStatusCode.OK, added);
        Assert.Equal(key.Fingerprint, Text(addedBody, "key_id"));
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_signatures", ServerRole.Admin, new { value = true })).Status);
        var trust = (await h.GetAsync("/api/v1/trust/integration")).Body!;
        Assert.True((bool)trust["required"]!);
        Assert.Equal([key.Fingerprint], trust["keys"]!.AsArray().Select(k => Text(k, "key_id")));

        // unsigned: refused in the runner's own words; forged (signed over another manifest) and unknown key: refused with the verifier's
        Assert.Equal(HttpStatusCode.Forbidden, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"))).Status);
        var (forged, forgedBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", Signed(package, key, manifestSigned: "something else"));
        Assert.Equal(HttpStatusCode.Forbidden, forged);
        Assert.Contains("does not hold", Text(forgedBody, "error"));
        var (stranger, strangerBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", Signed(package, ConsoleSigningKey.Generate()));
        Assert.Equal(HttpStatusCode.Forbidden, stranger);
        Assert.Contains("not one the group trusts", Text(strangerBody, "error"));
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync("/api/v1/definitions/integration/orders")).Status);

        // signed with the trusted key: accepted, recorded with its signer
        var (ok, published) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", Signed(package, key));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("verified", Text(published, "signature"));
        var stored = new DefinitionStore(h.State).Get(RunnerPublishing.Kind, "orders", 1)!;
        Assert.Equal(key.Fingerprint, stored.Signer);
        Assert.NotNull(stored.Signature);

        // a revoked key refuses new versions (and says why); rotating is adding another key
        Assert.Equal(HttpStatusCode.OK, (await h.CallAsync(HttpMethod.Post, $"/api/v1/trust/integration/{key.Fingerprint}/revoke", ServerRole.Admin, null)).Status);
        var next = FakePackage.Make("orders", "2");
        await next.UploadAllAsync(publisher);
        var (revoked, revokedBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", Signed(next, key));
        Assert.Equal(HttpStatusCode.Forbidden, revoked);
        Assert.Contains("revoked", Text(revokedBody, "error"));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Without_require_signatures_a_version_is_accepted_unsigned_but_a_signature_that_is_there_is_still_checked(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(publisher);
        var (bad, badBody) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", Signed(package, ConsoleSigningKey.Generate()));
        Assert.Equal(HttpStatusCode.Forbidden, bad);   // a signature from a key nobody trusts is not ignored
        Assert.Contains("not one the group trusts", Text(badBody, "error"));
        Assert.Equal(HttpStatusCode.OK, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"))).Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_version_gate_of_an_instance_refuses_an_unsigned_version_only_when_require_signatures_is_on_and_verifies_a_signature_that_is_there(string engine)
    {
        await using var h = new RunnerHarness(engine);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(publisher);
        Assert.Equal(HttpStatusCode.OK, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "stage"))).Status);
        var gate = RunnerVersionGate.Create(h.State);
        var version = new DefinitionStore(h.State).Get(RunnerPublishing.Kind, "orders", 1)!;
        Assert.Null(gate(version));   // unsigned, and nothing requires signatures

        await h.CallAsync(HttpMethod.Put, "/api/v1/settings/require_signatures", ServerRole.Admin, new { value = true });
        Assert.Contains("is unsigned and signatures are required for integration", gate(version));
        // a signature that does not verify is refused whatever the switch says
        Assert.Contains("is not one the group trusts", gate(version with { Signature = DefinitionSigning.Sign(ConsoleSigningKey.Generate(), "integration", "orders", version.Content, []) }));
    }
}
