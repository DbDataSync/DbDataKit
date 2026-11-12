using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>Plan 30 PB06 against two real runner servers: a version goes from one group to another over their HTTP APIs, by hash.</summary>
public sealed class PublishPromotionTests
{
    private sealed class Link(PublisherClient client, string name, ServerRole role) : IDefinitionsLink
    {
        public string Name { get; } = name;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct) => client.RawAsync(method, "/api/v1/" + path, content, role);
    }

    [Fact]
    public async Task A_version_moves_to_another_runner_group_sending_only_the_files_it_lacks_and_an_unbound_slot_or_an_untrusted_key_refuses()
    {
        await using var test = new RunnerHarness(StateEngineIds.Sqlite);
        await using var production = new RunnerHarness(StateEngineIds.Sqlite);
        await test.StartAsync();
        await production.StartAsync();
        using var from = await PublisherClient.PairAsync(test, "builder");
        using var fromAdmin = await PublisherClient.PairAsync(test, "reader", ServerRole.Admin);
        using var to = await PublisherClient.PairAsync(production, "promoter", ServerRole.Admin);
        var package = FakePackage.Make("orders");
        await package.UploadAllAsync(from);
        Assert.Equal(HttpStatusCode.OK, (await from.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", package.Body("dev", "serve"))).Status);

        var source = new Link(fromAdmin, "test group", ServerRole.Admin);
        var target = new Link(to, "production group", ServerRole.Admin);
        var promoter = new DefinitionPromoter();
        var request = new PromoteRequest("integration", "orders", "dev", "prod", Mode: "stage");
        var first = await promoter.PromoteAsync(source, target, request);
        Assert.Equal((2, 0), (first.Sent, first.Skipped));
        Assert.Equal(1, first.TargetVersion);
        Assert.Equal("stage", first.Mode);
        var described = (await to.SendAsync(HttpMethod.Get, "/api/v1/definitions/integration/orders", role: ServerRole.Admin)).Body!;
        Assert.Equal(1, (int)described["channels"]!.AsArray().Single(c => (string)c!["channel"]! == "prod")!["staged"]!);

        var again = await promoter.PromoteAsync(source, target, request);
        Assert.Equal((0, 2), (again.Sent, again.Skipped));
        Assert.False(again.Changed);

        // the file the target holds is the one the source holds
        using var response = await to.RawAsync(HttpMethod.Get, "/api/v1/artifacts/" + FakePackage.Sha(package.Files["notes.txt"]), null, ServerRole.Admin);
        Assert.Equal("notes 1", await response.Content.ReadAsStringAsync());

        // a group that requires signatures and does not trust the signer refuses the version
        var key = ConsoleSigningKey.Generate();
        var next = FakePackage.Make("orders", "2");
        await next.UploadAllAsync(from);
        var signed = JsonSerializer.SerializeToNode(next.Body("dev", "serve"))!.AsObject();
        signed["signature"] = DefinitionSigning.Sign(key, "integration", "orders", next.Manifest(), next.Files.Select(f => new ArtifactRef(f.Key, FakePackage.Sha(f.Value), f.Value.Length, null)));
        await test.CallAsync(HttpMethod.Put, "/api/v1/trust/integration/keys", ServerRole.Admin, new { public_key = Url.Encode(key.PublicKey), label = "ci" });
        Assert.Equal(HttpStatusCode.OK, (await from.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", signed)).Status);
        var refused = await Assert.ThrowsAsync<DefinitionPromotionException>(() => promoter.PromoteAsync(source, target, request));
        Assert.Contains("production group refused", refused.Message);
        Assert.Contains("not one the group trusts", refused.Message);
        await production.CallAsync(HttpMethod.Put, "/api/v1/trust/integration/keys", ServerRole.Admin, new { public_key = Url.Encode(key.PublicKey), label = "ci" });
        var second = await promoter.PromoteAsync(source, target, request);
        Assert.Equal(2, second.SourceVersion);
        Assert.Equal(2, second.TargetVersion);
        Assert.Equal("verified", second.Signature);
    }
}
