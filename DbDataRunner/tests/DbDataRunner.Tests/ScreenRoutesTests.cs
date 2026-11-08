using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>The calls the Runner screens make beyond the first ones (plan 33 UE08): the connection slots a package's manifests declare for each channel (so the bindings editor lists them).</summary>
public sealed class ScreenRoutesTests
{
    private static string Text(JsonNode? node, string key) => (string)node![key]!;

    private static IReadOnlyList<AuditRecord> Audited(RunnerHarness h, string verb, string? outcome = null) =>
        [.. h.Audit.Read(new AuditQuery(Verb: verb, Limit: 200)).Where(r => outcome is null || r.Entry.Outcome == outcome)];

    [Fact]
    public async Task The_definition_detail_names_the_slots_each_channel_needs_from_the_served_and_the_staged_version()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        using var publisher = await PublisherClient.PairAsync(h, "builder-a");

        PackageTask Task(string name, params ConnectionSlot[] slots) => SamplePackage.Task(name) with { Connections = slots };
        await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { source = "erp" } });
        var one = FakePackage.Make("orders", "1", Task("load", new ConnectionSlot("source", "sqlserver")));
        await one.UploadAllAsync(publisher);
        Assert.Equal(HttpStatusCode.OK, (await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", one.Body("dev", "serve"))).Status);

        // version 2 is staged and wants one more slot and a second task that uses the first
        await publisher.JsonAsync(HttpMethod.Put, "/api/v1/bindings/dev/integration/orders", new { slots = new { target = "warehouse" } });
        var two = FakePackage.Make("orders", "2", Task("load", new ConnectionSlot("source", "sqlserver"), new ConnectionSlot("target", "postgres")), Task("check", new ConnectionSlot("source", "sqlserver")));
        await two.UploadAllAsync(publisher);
        var (staged, body) = await publisher.JsonAsync(HttpMethod.Put, "/api/v1/definitions/integration/orders", two.Body("dev", "stage"));
        Assert.True(staged == HttpStatusCode.OK, body?.ToJsonString());

        var (_, detail) = await h.GetAsync("/api/v1/definitions/integration/orders");
        var slots = detail!["slots"]!.AsArray().Select(s => s!).ToList();
        Assert.Equal(["source", "target"], slots.Select(s => Text(s, "slot")));
        var source = slots.Single(s => Text(s, "slot") == "source");
        Assert.Equal("dev", Text(source, "channel"));
        Assert.Equal("sqlserver", Text(source, "kind"));
        Assert.Equal(["check", "load"], source["tasks"]!.AsArray().Select(t => (string)t!));
        Assert.Equal([1, 2], source["versions"]!.AsArray().Select(v => (int)v!));
        Assert.Equal([2], slots.Single(s => Text(s, "slot") == "target")["versions"]!.AsArray().Select(v => (int)v!));
    }
}
