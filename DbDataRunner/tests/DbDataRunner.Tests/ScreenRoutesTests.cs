using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>The calls the Runner screens make beyond the first ones (plan 33 UE08, UE11): the connection slots a package's manifests declare for each channel (so the bindings editor lists them) and the labels of an instance.</summary>
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

    [Fact]
    public async Task Labels_of_an_instance_are_read_by_a_viewer_and_set_as_a_whole_by_an_administrator_with_every_change_audited()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var path = $"/api/v1/instances/{h.Engine.InstanceId}/labels";

        var (read, before) = await h.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read);
        Assert.Contains(before!["labels"]!.AsArray(), l => Text(l, "label").StartsWith("os:", StringComparison.Ordinal) && Text(l, "source") == "reported");
        Assert.DoesNotContain(before["labels"]!.AsArray(), l => Text(l, "source") == "admin");

        // an operator is not enough, and the refusal is on record
        var (operatorStatus, refused) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Operator, new { labels = new[] { "gpu" } });
        Assert.Equal(HttpStatusCode.Forbidden, operatorStatus);
        Assert.Contains("admin", Text(refused, "error"));
        Assert.Single(Audited(h, "runner.label.set", AuditEvent.Refused));
        Assert.DoesNotContain(((await h.GetAsync(path)).Body!["labels"]!.AsArray()), l => Text(l, "label") == "gpu");

        // the whole list is the answer: labels are added, spacing around a colon is normalised
        var (status, set) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { labels = new[] { "gpu", "site : oslo" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpu", "site:oslo"], set!["added"]!.AsArray().Select(l => (string)l!));
        Assert.Empty(set["removed"]!.AsArray());
        var admin = (await h.GetAsync(path)).Body!["labels"]!.AsArray().Where(l => Text(l, "source") == "admin").Select(l => Text(l, "label")).Order().ToList();
        Assert.Equal(["gpu", "site:oslo"], admin);

        // a shorter list removes what it leaves out; the reported labels stay
        var (_, shorter) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { labels = new[] { "gpu" } });
        Assert.Equal(["site:oslo"], shorter!["removed"]!.AsArray().Select(l => (string)l!));
        Assert.Contains(shorter["labels"]!.AsArray(), l => Text(l, "label").StartsWith("os:", StringComparison.Ordinal));
        var (_, unchanged) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { labels = new[] { "gpu" } });
        Assert.Empty(unchanged!["added"]!.AsArray());
        Assert.Empty(unchanged["removed"]!.AsArray());

        Assert.Equal(["gpu", "site:oslo"], Audited(h, "runner.label.add", AuditEvent.Ok).Select(a => a.Entry.Detail!.Contains("gpu") ? "gpu" : "site:oslo").Order());
        Assert.Single(Audited(h, "runner.label.remove", AuditEvent.Ok));
        Assert.All(Audited(h, "runner.label.add"), a => Assert.Equal(h.Engine.InstanceId!.Value.ToString(), a.Entry.TargetId));
    }

    [Theory]
    [InlineData("""{"labels":["has space"]}""", "is not a label")]
    [InlineData("""{"labels":["-leading"]}""", "is not a label")]
    [InlineData("""{"labels":[""]}""", "is not a label")]
    [InlineData("""{"labels":["os:linux","os:windows","network:outside"]}""", "reported by the runner itself")]
    [InlineData("""{"labels":"gpu"}""", "The body is")]
    [InlineData("""{"labels":[1]}""", "The body is")]
    [InlineData("""{}""", "The body is")]
    public async Task A_label_list_that_is_not_one_is_refused_with_the_reason_and_nothing_changes(string json, string reason)
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var path = $"/api/v1/instances/{h.Engine.InstanceId}/labels";
        await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { labels = new[] { "keep" } });
        // the reported label of this machine is the one to name in the reported case
        var reported = (await h.GetAsync(path)).Body!["labels"]!.AsArray().First(l => Text(l, "source") == "reported");
        var text = json.Replace("os:linux", Text(reported, "label"));

        var (status, body) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, JsonNode.Parse(text));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(reason, Text(body, "error"));
        Assert.Equal(["keep"], (await h.GetAsync(path)).Body!["labels"]!.AsArray().Where(l => Text(l, "source") == "admin").Select(l => Text(l, "label")));
    }

    [Fact]
    public async Task Too_many_labels_and_an_unknown_instance_are_refused()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var path = $"/api/v1/instances/{h.Engine.InstanceId}/labels";
        var many = Enumerable.Range(0, RunnerGroupApi.MaxAdminLabels + 1).Select(i => "l" + i).ToArray();
        var (tooMany, body) = await h.CallAsync(HttpMethod.Put, path, ServerRole.Admin, new { labels = many });
        Assert.Equal(HttpStatusCode.BadRequest, tooMany);
        Assert.Contains("at most 32", Text(body, "error"));

        Assert.Equal(HttpStatusCode.NotFound, (await h.CallAsync(HttpMethod.Put, $"/api/v1/instances/{Guid.NewGuid()}/labels", ServerRole.Admin, new { labels = new[] { "gpu" } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.GetAsync("/api/v1/instances/not-a-guid/labels")).Status);
    }
}
